# Building ssms-patcher from source

Requires .NET SDK 10+ on Linux.

```
cd src
dotnet publish -c Release -r linux-x64 --self-contained -o ../bin
mv ../bin/ssms-patcher.dll ../bin/.ssms-patcher.dll  # keep the native ELF only
# Actually the ELF is named 'ssms-patcher' — just keep that one.
rm -f ../bin/*.pdb ../bin/ssms-patcher.dll
```

The published binary is a self-contained ELF with the .NET 10 runtime
bundled (~73 MB). No .NET install needed on the target machine.
