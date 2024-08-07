using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.AccessControl;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Mocker
{
    internal class Program
    {
        static int Main(string[] args)
        {
            var targetDll = args[0];
            if (targetDll.StartsWith("@")) // msbuild bundled the arguments.
            {
                args = File.ReadAllText(targetDll[1..]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                targetDll = args[0];
            }

            var paths = new HashSet<string>(args.Skip(1).Select(Path.GetFullPath));
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var libPath = Path.Combine(folder, "Mocker.API.dll");
            var moqLibPath = paths.Single(s => s.EndsWith("Mocker.API.dll"));
            paths.Add(Path.GetFullPath(targetDll));
            var toWeave = RunWeave(libPath, targetDll, moqLibPath, paths);
            if (toWeave is null) return 1;
            foreach (var (origin, dest) in toWeave)
            {
                File.Move(origin, dest, true);
            }
            return 0;
        }

        private static Dictionary<string, string>? RunWeave(string libPath, string dllPath, string moqLibPath, HashSet<string> paths)
        {
            using var mockerApi = ModuleDefinition.ReadModule(libPath);
            using var module = ModuleDefinition.ReadModule(dllPath);
            using var moqModule = ModuleDefinition.ReadModule(moqLibPath);

            Environment.CurrentDirectory = Path.GetDirectoryName(dllPath)!;
            var typesToMock = GetMockedTypes(module, ["Moq.Mock`1"]).ToArray();



            var modulesToWeave = new HashSet<ModuleDefinition>();
            var error = false;
            foreach (var type in typesToMock.Distinct())
            {
                var theType = type.Resolve();
                if (IsAlreadyWeaved(theType.Module, mockerApi)) continue;
                modulesToWeave.Add(theType.Module);

                var typeModulePath = Path.GetFullPath(theType.Module.FileName);
                //is type dll present in paths ?
                if (!paths.Contains(typeModulePath))
                {
                    Console.WriteLine($"error Mocker.Weaver: Cannot Mock '{theType.FullName}' in dll {typeModulePath} because this shared dll.");
                    error = true;
                    continue;
                }
                WeaveType(mockerApi, theType.Module, theType);
            }

            if (error) return null;
            var guid = Guid.NewGuid();
            var weavedPaths = new Dictionary<string, string>();
            foreach (var moduleToWeave in modulesToWeave)
            {
                var newPath = moduleToWeave.FileName + guid;
                moduleToWeave.Write(newPath);
                weavedPaths[newPath] = moduleToWeave.FileName;
            }

            return weavedPaths;
        }

        static void WeaveType(ModuleDefinition libModule, ModuleDefinition module, TypeDefinition type)
        {
            var mockProxyType = libModule.GetType("Mocker.API.MockProxy");
            var proxyMethod = mockProxyType.Methods.Single(x => x.Name == "Relay");
            var objCtor = module.ImportReference(module.TypeSystem.Object.Resolve().Methods.Single(x => x.Name == ".ctor"));
            var importedProxyType = module.ImportReference(mockProxyType);

            var proxyField = new FieldDefinition("mockProxy", Mono.Cecil.FieldAttributes.Public, importedProxyType);
            type.Fields.Add(proxyField);

            var proxyConstructor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            type.Methods.Add(proxyConstructor);
            var parameter = new ParameterDefinition("relay", Mono.Cecil.ParameterAttributes.None, importedProxyType);
            proxyConstructor.Parameters.Add(parameter);
            var ctorIl = proxyConstructor.Body.GetILProcessor();
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, objCtor);
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg_1);
            ctorIl.Emit(OpCodes.Stfld, proxyField);
            ctorIl.Emit(OpCodes.Ret);

            // Capture all original methods names
            var redirectedMethods = type.Methods
                .Where(predicate => !predicate.IsConstructor && !predicate.IsStatic && !predicate.IsAbstract)
                .ToArray();
            var originals = new Dictionary<string, MethodDefinition>();
            // clone methods into Method_Original

            foreach (var method in redirectedMethods)
            {
                if (method.IsAbstract || method.IsStatic) continue;
                var newMethod = new MethodDefinition(method.Name + "_Original", method.Attributes, method.ReturnType);
                originals.Add(method.FullName, newMethod);
                type.Methods.Add(newMethod);
                foreach (var curr in method.Parameters)
                {
                    newMethod.Parameters.Add(new ParameterDefinition(curr.Name, curr.Attributes, curr.ParameterType));
                }
                foreach (var instruction in method.Body.Instructions)
                {
                    newMethod.Body.Instructions.Add(instruction);
                }
            }



            // Create new methods to redirect the original method
            foreach (var method in redirectedMethods)
            {
                method.Body.Instructions.Clear();
                var il = method.Body.GetILProcessor();

                var elseStart = il.Create(OpCodes.Ldarg_0);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, proxyField);
                il.Emit(OpCodes.Brtrue_S, elseStart); //0x0f
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, originals[method.FullName]);
                il.Emit(OpCodes.Ret);
                il.Append(elseStart);

                il.Emit(OpCodes.Ldfld, proxyField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldftn, method);

                var types = method.Parameters.Select(x => typeof(object)).Append(method.ReturnType == method.Module.TypeSystem.Void ? typeof(void) : typeof(object)).ToArray();
                var delegateType = Expression.GetDelegateType(types);
                if (delegateType.ContainsGenericParameters)
                {
                    delegateType = delegateType.GetGenericTypeDefinition();
                }
                var delegateConstructor = module.ImportReference(delegateType.GetConstructors()[0]);

                il.Emit(OpCodes.Newobj, delegateConstructor);
                il.Emit(OpCodes.Ldc_I4, method.Parameters.Count);
                il.Emit(OpCodes.Newarr, module.TypeSystem.Object);

                for (var i = 0; i < method.Parameters.Count; i++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    if (method.Parameters[i].ParameterType.IsValueType)
                    {
                        il.Emit(OpCodes.Box, method.Parameters[i].ParameterType);
                    }
                    il.Emit(OpCodes.Stelem_Ref);
                }

                il.Emit(OpCodes.Callvirt, module.ImportReference(proxyMethod));
                if (method.ReturnType.IsValueType)
                {
                    il.Emit(OpCodes.Unbox_Any, method.ReturnType);
                }
                else if (method.ReturnType.IsByReference)
                {
                    il.Emit(OpCodes.Castclass, method.ReturnType);
                }
                il.Emit(OpCodes.Ret);
            }
        }

        static IEnumerable<TypeReference> GetMockedTypes(ModuleDefinition module, HashSet<string> mockingType)
        {
            foreach (var type in module.GetAllTypes())
            {
                // Scan methods
                foreach (var method in type.Methods)
                {
                    foreach (var found in ScanMethodForMockReferences(method, mockingType))
                    {
                        yield return found;
                    }
                }

                // Scan properties
                foreach (var property in type.Properties)
                {
                    if (property.GetMethod != null)
                    {
                        foreach (var found in ScanMethodForMockReferences(property.GetMethod, mockingType))
                        {
                            yield return found;
                        }
                    }

                    if (property.SetMethod != null)
                    {
                        foreach (var found in ScanMethodForMockReferences(property.SetMethod, mockingType))
                        {
                            yield return found;
                        }
                    }
                }
            }
        }

        static IEnumerable<TypeReference> ScanMethodForMockReferences(MethodDefinition method, HashSet<string> mockingType)
        {
            if (method.HasBody)
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code == Code.Newobj)
                    {
                        if (instruction.Operand is MethodReference methodReference && mockingType.Contains(methodReference.DeclaringType.GetElementType().FullName))
                        {
                            var genericType = (GenericInstanceType)methodReference.DeclaringType;
                            yield return genericType.GenericArguments[0];
                        }
                    }
                }
            }
        }

        static bool IsAlreadyWeaved(ModuleDefinition module, ModuleDefinition mockerApi)
        {
            // Check if MockerWeavingSentinelAttribute is present on the assembly
            var sentinelAttribute = module.Assembly.CustomAttributes
                .FirstOrDefault(attr => attr.AttributeType.FullName == "Mocker.API.MockerWeavingSentinelAttribute");

            if (sentinelAttribute != null)
            {
                Console.WriteLine("MockerWeavingSentinelAttribute found on the assembly. Exiting.");
                return true;
            }

            // Add MockerWeavingSentinelAttribute to the assembly
            var attributeTypeName = "Mocker.API.MockerWeavingSentinelAttribute";

            var attributeType = mockerApi.Types.First(t => t.FullName == attributeTypeName);
            var attributeTypeReference = module.ImportReference(attributeType);
            var attributeConstructor = attributeType.Methods.FirstOrDefault(m => m.IsConstructor && !m.HasParameters)
                ?? throw new InvalidOperationException($"Parameterless constructor for type '{attributeTypeName}' not found.");
            var attributeConstructorReference = module.ImportReference(attributeConstructor);
            var customAttribute = new CustomAttribute(attributeConstructorReference);

            module.Assembly.CustomAttributes.Add(customAttribute);
            return false;
        }
    }
}
