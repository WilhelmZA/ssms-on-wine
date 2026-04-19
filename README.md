# SSMS 20 on Wine — install package

This is an installer bundle for running **SQL Server Management Studio 20.2.1**
on Linux via Wine. It automates everything needed to go from a blank Wine
prefix to a working SSMS with Object Explorer, Query Editor, and Windows
Authentication via Kerberos.

Tested against: Ubuntu 24.04 + wine-stable 11.0 + SQL Server 2022 (16.0).

## What you need first

1. **Wine 11.0 or newer** from the official WineHQ repo
   (the Ubuntu-shipped `wine` package is too old):
   ```
   sudo apt install wine-stable winetricks unzip
   ```
   On Ubuntu 24.04, first add WineHQ's Noble repo — see
   <https://wiki.winehq.org/Ubuntu>.

2. **`SSMS-Setup-ENU.exe`** — download from Microsoft:
   <https://learn.microsoft.com/en-us/sql/ssms/download-sql-server-management-studio-ssms>
   (We cannot redistribute this. Download it once and keep it around.)

3. **Kerberos configured on the Linux host** if you need Windows
   Authentication. Typically: join the domain (SSSD/realmd) or configure
   `/etc/krb5.conf` with your realm. You need `kinit` to work on the host
   — Wine will pick up the ticket cache automatically via its SSPI stack.

## Install

Clone or download this repo:
```
git clone https://github.com/WilhelmZA/ssms-on-wine.git
cd ssms-on-wine
```

Then run the installer, pointing it at your downloaded SSMS installer:
```
./setup-ssms.sh /path/to/SSMS-Setup-ENU.exe
```

By default this creates a Wine prefix at `~/.wine-ssms`. To use a different
location:

```
./setup-ssms.sh /path/to/SSMS-Setup-ENU.exe /custom/prefix/path
```

The script takes 10-15 minutes to run. Most of the time is the .NET 4.8
install and the MS SSMS installer. The patching steps are fast.

The `ssms-patcher` binary is auto-downloaded from the GitHub Release on
first run. If you'd rather build it yourself (e.g. for transparency):
```
# requires dotnet-sdk-10 or newer
make build
```

## Run

1. Get a Kerberos ticket on the **host** (not inside Wine):
   ```
   kinit your.username@YOURREALM.LOCAL
   klist     # verify you have a ticket
   ```
2. Launch SSMS from your app menu ("SQL Server Management Studio 20 (Wine)"),
   or manually:
   ```
   WINEPREFIX=~/.wine-ssms wine \
     "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe"
   ```
3. Connect Dialog → Windows Authentication → enter your server name.

## What gets installed

Into the Wine prefix:

- .NET Framework 4.8 (via `winetricks dotnet48`)
- VC++ 2022 runtimes
- MS gdiplus + windowscodecs (natively, though see note below)
- CoreFonts, D3DX9, DXVK, MSXML6
- SSMS 20.2.1 itself (via the Microsoft bundled installer)
- 7 bundled .NET dependency DLLs that SSMS needs but doesn't ship correctly
  (`System.Text.Json`, `Microsoft.Bcl.AsyncInterfaces`, `System.Text.Encodings.Web`,
  `System.Memory`, and 3 `System.Security.*`)

Binary patches applied to the installed SSMS:

- **GIF → PNG resource swap** in ~35 SSMS DLLs. Wine 11's GIF decoder is
  broken (filed upstream); replacing the embedded GIF resources with PNG
  bytes of the same image makes every SSMS dialog that loads icons work.
  Originals backed up as `*.orig-gif`.

- **NavigationService no-op** in
  `Microsoft.SqlServer.Management.SqlStudio.Explorer.dll`. Works around a
  VS Shell service-container issue under Wine that otherwise leaves the
  Object Explorer tree empty. Original backed up as `*.preinject`.

- **Assembly binding redirects** in both `Ssms.exe.config` files (the
  one in the IDE folder and the auto-generated one in AppData).

## Troubleshooting

**Object Explorer shows no databases.**
Verify the patch is in place:
```
~/apps/wine/SQL-SSMS/bin/ssms-patcher verify \
  ~/.wine-ssms/drive_c/Program\ Files\ \(x86\)/Microsoft\ SQL\ Server\ Management\ Studio\ 20/Common7/IDE
```
You should see non-zero "Nav-patched DLLs".

**Dialogs throw "Parameter is not valid. (System.Drawing)".**
A GIF somewhere wasn't patched. Re-run:
```
~/apps/wine/SQL-SSMS/bin/ssms-patcher patch-gifs <IDE_DIR>
```

**Kerberos "Login failed" / SSPI error.**
Check `klist` on the host — if empty, run `kinit`. If the service principal
`MSSQLSvc/<server>:<port>@REALM` is missing from the ticket, your Kerberos
config on the host is incomplete (ask your AD admin).

**See the actual error SSMS hit.**
Inside SSMS: `View → Output`, then pick "Object Explorer" in the dropdown.
SSMS's own catch blocks log here (we've enabled this by default).

**Revert everything.**
```
~/apps/wine/SQL-SSMS/bin/ssms-patcher restore <IDE_DIR>
```

## What works and what doesn't

See `APPDB-ENTRY.txt` next to this README (the WineHQ AppDB write-up) for
the full list. Short version:

✅ Works: connect, Object Explorer tree, Query Editor, Server Properties,
  Database Mail, most menus and dialogs
❌ Doesn't work: Azure connection types, SSAS/SSRS/SSIS designers,
  Always On dialogs, Database Engine Tuning Advisor (mostly untested —
  see AppDB entry)

## Uninstall

```
~/apps/wine/SQL-SSMS/bin/ssms-patcher restore <IDE_DIR>
rm -rf ~/.wine-ssms
rm ~/.local/share/applications/ssms-on-wine.desktop
```

## Layout of this package

```
SQL-SSMS/
├── README.md                    this file
├── setup-ssms.sh                main installer script
├── APPDB-ENTRY.txt              WineHQ AppDB compatibility writeup
├── bin/
│   └── ssms-patcher             self-contained Linux x86_64 ELF binary
│                                (Mono.Cecil + ImageSharp, ~73 MB)
├── dlls/                        .NET DLLs SSMS needs, bundled
│   ├── System.Text.Json.dll
│   ├── System.Memory.dll
│   └── ...
└── src/                         source of the patcher
    ├── Program.cs
    └── ssms-patcher.csproj
```

## Issues

Open an issue at
<https://github.com/WilhelmZA/ssms-on-wine/issues> if you hit something
the troubleshooting section doesn't cover. Include your wine version
(`wine --version`), Ubuntu version (`lsb_release -ds`), and the SSMS
Output Window → Object Explorer pane contents if it's an OE problem.

## License

The patcher code in `src/` is MIT. The bundled .NET DLLs in `dlls/` are
redistributable per their respective MS licenses (all are open-source
.NET Foundation packages from NuGet). You must provide your own
`SSMS-Setup-ENU.exe`; SSMS itself is governed by Microsoft's EULA.
