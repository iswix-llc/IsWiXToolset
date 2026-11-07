# ApiContract

Keeps a replacement `WixToolset.Core.Native` binary-compatible with the original.

1. Build the fork's `src/wix` as usual, then find the built `WixToolset.Core.Native.dll`
   (the netstandard2.0 one, e.g. `build\wix\obj\WixToolset.Core.Native\Debug\netstandard2.0`). It needs
   `WixToolset.Data.dll` to load; if that isn't in the same folder, add
   `--probe build\api\Debug\netstandard2.0` to the command.
2. Create the baseline once, before changing Core.Native, and commit it:

       dotnet run --project src/tools/ApiContract -- dump <path>\WixToolset.Core.Native.dll src\tools\ApiContract\core-native.baseline.txt

3. After changing Core.Native, check it (exit code 1 lists what disappeared or changed):

       dotnet run --project src/tools/ApiContract -- compare src\tools\ApiContract\core-native.baseline.txt <path>\WixToolset.Core.Native.dll

Members may be added; none may be removed or changed. Parameter names are not compared (binary
compatibility only).
