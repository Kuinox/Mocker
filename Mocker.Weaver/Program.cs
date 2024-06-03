﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using Mocker.API;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Mocker
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var guid = Guid.NewGuid().ToString();
            var libPath = @"C:\dev\Mocker\Mocker.API\bin\Debug\net8.0\Mocker.API.dll";
            var moqPath = @"C:\dev\Mocker\Mocker.Moq\bin\Debug\net8.0\Mocker.Moq.dll";
            var dllPath = @"C:\dev\Mocker\Mocker.Moq.Tests\bin\Debug\net8.0\Mocker.Moq.Tests.dll";
            var tempDllPath = dllPath + guid;

            using (var moqPEStream = File.Open(moqPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var moqModule = ModuleDefinition.ReadModule(moqPEStream))
            using (var libPEStream = File.Open(libPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var libModule = ModuleDefinition.ReadModule(libPEStream))
            using (var peStream = File.Open(dllPath, FileMode.Open, FileAccess.ReadWrite))
            using (var module = ModuleDefinition.ReadModule(peStream))
            {
                if (!ShouldWeave(module)) return;

                var moqType = moqModule.GetType("Moq.Mock`1");
                var mockProxyType = libModule.GetType("Mocker.API.MockProxy");
                var objectCtor = module.ImportReference(module.TypeSystem.Object.Resolve().Methods.Single(x => x.Name == ".ctor"));
                var typesToMock = GetMockedTypes(module, [moqType.FullName]).ToArray();


                var proxyMethod = mockProxyType.Methods.Single(x => x.Name == "Relay");
                var importedProxyType = module.ImportReference(mockProxyType);

                foreach (var type in typesToMock)
                {
                    var theType = type.Resolve();
                    WeaveType(theType.Module,theType , proxyMethod, importedProxyType, objectCtor);
                }


                module.Write(tempDllPath);
            }

            File.Move(tempDllPath, dllPath, true);
        }

        static void WeaveType(ModuleDefinition module, TypeDefinition type, MethodDefinition proxyMethod, TypeReference importedProxyType, MethodReference objCtor)
        {
            var proxyField = new FieldDefinition("mockProxy", FieldAttributes.Public, importedProxyType);
            type.Fields.Add(proxyField);

            var proxyConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            type.Methods.Add(proxyConstructor);
            var parameter = new ParameterDefinition("relay", ParameterAttributes.None, importedProxyType);
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
                .Where(predicate => !predicate.IsConstructor && !predicate.IsStatic)
                .ToArray();
            var originals = new Dictionary<string, MethodDefinition>();
            // clone methods into Method_Original

            foreach (var method in redirectedMethods)
            {
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

        static bool ShouldWeave(ModuleDefinition module)
        {
            // Check if MockerWeavingSentinelAttribute is present on the assembly
            var sentinelAttribute = module.CustomAttributes
                .FirstOrDefault(attr => attr.AttributeType.FullName == typeof(Mocker.API.MockerWeavingSentinelAttribute).FullName);

            if (sentinelAttribute != null)
            {
                Console.WriteLine("MockerWeavingSentinelAttribute found on the assembly. Exiting.");
                return false;
            }

            // Add MockerWeavingSentinelAttribute to the assembly
            var attributeConstructor = module.ImportReference(typeof(Mocker.API.MockerWeavingSentinelAttribute).GetConstructor(Type.EmptyTypes));
            var customAttribute = new CustomAttribute(attributeConstructor);
            module.Assembly.CustomAttributes.Add(customAttribute);
            return true;
        }
    }
}
