$currentConfig = 'Debug'

Remove-Item -Path "artifacts\*" -Recurse
dotnet nuget locals all --clear

function Remove-BinObjFirstLevel {
    Get-ChildItem -Path . -Directory | Where-Object {
        $_.Name -notmatch '\.git'
    } | ForEach-Object {
        $binPath = "$($_.FullName)\bin"
        $objPath = "$($_.FullName)\obj"

        if (Test-Path -Path $binPath) {
            Remove-Item -Path $binPath -Recurse -Force
            Write-Output "Removed bin folder: $binPath"
        }

        if (Test-Path -Path $objPath) {
            Remove-Item -Path $objPath -Recurse -Force
            Write-Output "Removed obj folder: $objPath"
        }
    }
}

# Execute the function to remove bin and obj folders in the first level of subfolders
Remove-BinObjFirstLevel


dotnet build Mocker.Weaver/Mocker.Weaver.csproj -c $currentConfig
dotnet pack Mocker.API/Mocker.API.csproj -c $currentConfig -o artifacts
dotnet pack Mocker.Moq/Mocker.Moq.csproj -c $currentConfig -o artifacts
dotnet build Mocker.Tests.sln -bl