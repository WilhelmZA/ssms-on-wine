#!/usr/bin/env bash
#
# setup-ssms.sh — install SQL Server Management Studio 20 under Wine on Linux.
#
# Prerequisites (the script checks these):
#   - wine-stable 11.0+ from WineHQ
#   - winetricks
#   - unzip, curl (for any dynamic bits, though we ship the DLLs)
#
# You also need:
#   - SSMS-Setup-ENU.exe (download from Microsoft — this script will NOT
#     fetch it for you, because MS EULA)
#
# Usage:
#   ./setup-ssms.sh /path/to/SSMS-Setup-ENU.exe [WINEPREFIX]
#
# If WINEPREFIX is omitted, ~/.wine-ssms is used.
#
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
INSTALLER="${1:-}"
export WINEPREFIX="${2:-$HOME/.wine-ssms}"
export WINEARCH=win64

log()  { printf '\033[1;34m[ssms-setup]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[ssms-setup]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[ssms-setup] FATAL:\033[0m %s\n' "$*" >&2; exit 1; }

# -----------------------------------------------------------------
# preflight
# -----------------------------------------------------------------
[ -n "$INSTALLER" ] || die "usage: $0 /path/to/SSMS-Setup-ENU.exe [WINEPREFIX]"
[ -f "$INSTALLER" ] || die "installer not found: $INSTALLER"
command -v wine >/dev/null 2>&1 || die "wine not installed. Install wine-stable from WineHQ first."
command -v winetricks >/dev/null 2>&1 || die "winetricks not installed (sudo apt install winetricks)."
command -v unzip >/dev/null 2>&1 || die "unzip required (sudo apt install unzip)."

WINE_VER="$(wine --version 2>/dev/null || true)"
log "wine version: $WINE_VER"
case "$WINE_VER" in
  wine-11.*) : ;;
  *) warn "untested wine version. Known-working: wine-11.0 (stable). Continuing anyway." ;;
esac

PATCHER="$SCRIPT_DIR/bin/ssms-patcher"
if [ ! -x "$PATCHER" ]; then
    log "patcher binary not found at $PATCHER"
    log "attempting to download latest release from GitHub..."
    mkdir -p "$SCRIPT_DIR/bin"
    RELEASE_URL="https://github.com/WilhelmZA/ssms-on-wine/releases/latest/download/ssms-patcher-linux-x64"
    if curl -sSL -f -o "$PATCHER" "$RELEASE_URL"; then
        chmod +x "$PATCHER"
        log "downloaded: $PATCHER"
    else
        warn "download failed. You can build the patcher yourself: cd $SCRIPT_DIR && make build"
        die  "cannot proceed without ssms-patcher binary."
    fi
fi

# -----------------------------------------------------------------
# create / update prefix
# -----------------------------------------------------------------
log "using WINEPREFIX=$WINEPREFIX"
if [ ! -d "$WINEPREFIX" ]; then
    log "creating fresh wine prefix..."
    wineboot --init >/dev/null 2>&1
    sleep 2
fi

# remove Wine Mono — we need real MS .NET Framework 4.8, not mono
log "applying winetricks verbs (this is the slow bit)..."
winetricks -q --force \
    remove_mono \
    win10 \
    dotnet48 \
    vcrun2022 \
    gdiplus \
    windowscodecs \
    corefonts \
    d3dcompiler_43 d3dcompiler_47 d3dx9 \
    msxml6 \
  >/dev/null 2>&1 || warn "winetricks returned non-zero (may just mean 'already installed')."

# -----------------------------------------------------------------
# run SSMS installer
# -----------------------------------------------------------------
IDE_DIR="$WINEPREFIX/drive_c/Program Files (x86)/Microsoft SQL Server Management Studio 20/Common7/IDE"
if [ -f "$IDE_DIR/Ssms.exe" ]; then
    log "SSMS already installed at: $IDE_DIR"
else
    log "running SSMS installer (this will open a Wine window; click through it)..."
    log "  installer: $INSTALLER"
    wine "$INSTALLER" /install /quiet /norestart || warn "installer exit code non-zero (check if SSMS nonetheless landed)."
    [ -f "$IDE_DIR/Ssms.exe" ] || die "SSMS did not install correctly. Re-run the installer interactively to see errors."
fi

# -----------------------------------------------------------------
# copy bundled NuGet DLLs
# -----------------------------------------------------------------
log "installing bundled .NET dependency DLLs into IDE/..."
for dll in "$SCRIPT_DIR"/dlls/*.dll; do
    cp -f "$dll" "$IDE_DIR/$(basename "$dll")"
    log "  + $(basename "$dll")"
done

# -----------------------------------------------------------------
# add binding redirects to BOTH Ssms.exe.config files
# -----------------------------------------------------------------
CFG_IDE="$IDE_DIR/Ssms.exe.config"
CFG_APPDATA="$WINEPREFIX/drive_c/users/$(whoami)/AppData/Local/Microsoft/SQL Server Management Studio/20.0_IsoShell/Ssms.exe.config"

inject_redirects_into() {
    local cfg="$1"
    [ -f "$cfg" ] || { warn "config not found (skipping): $cfg"; return 0; }

    # Don't double-inject
    if grep -q 'SSMS-ON-WINE-REDIRECTS' "$cfg"; then
        log "  (already injected) $cfg"
        return 0
    fi

    log "  injecting redirects into $cfg"
    python3 - "$cfg" <<'PYEOF'
import sys
p = sys.argv[1]
with open(p, 'r', encoding='utf-8') as f: content = f.read()
redirects = '''
      <!-- SSMS-ON-WINE-REDIRECTS START -->
      <dependentAssembly>
        <assemblyIdentity name="System.Text.Json" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral"/>
        <bindingRedirect oldVersion="0.0.0.0-7.0.0.1" newVersion="7.0.0.1"/>
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="Microsoft.Bcl.AsyncInterfaces" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral"/>
        <bindingRedirect oldVersion="1.0.0.0-7.0.0.0" newVersion="7.0.0.0"/>
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Text.Encodings.Web" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral"/>
        <bindingRedirect oldVersion="0.0.0.0-7.0.0.0" newVersion="7.0.0.0"/>
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral"/>
        <bindingRedirect oldVersion="0.0.0.0-4.0.1.2" newVersion="4.0.1.2"/>
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Security.AccessControl" publicKeyToken="b03f5f7f11d50a3a" culture="neutral"/>
        <bindingRedirect oldVersion="0.0.0.0-5.0.0.0" newVersion="5.0.0.0"/>
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.IO.FileSystem.AccessControl" publicKeyToken="b03f5f7f11d50a3a" culture="neutral"/>
        <bindingRedirect oldVersion="0.0.0.0-5.0.0.0" newVersion="5.0.0.0"/>
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Security.Principal.Windows" publicKeyToken="b03f5f7f11d50a3a" culture="neutral"/>
        <bindingRedirect oldVersion="0.0.0.0-5.0.0.0" newVersion="5.0.0.0"/>
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Threading.Tasks.Dataflow" publicKeyToken="b03f5f7f11d50a3a" culture="neutral"/>
        <bindingRedirect oldVersion="0.0.0.0-4.6.3.0" newVersion="4.5.24.0"/>
      </dependentAssembly>
      <!-- SSMS-ON-WINE-REDIRECTS END -->
'''
# Insert right before </assemblyBinding> (closing tag). If there's no explicit
# <assemblyBinding> block inside <runtime>, create one.
if '</assemblyBinding>' in content:
    content = content.replace('</assemblyBinding>', redirects + '   </assemblyBinding>', 1)
elif '<runtime>' in content:
    inject = '<assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">' + redirects + '</assemblyBinding>'
    content = content.replace('<runtime>', '<runtime>' + inject, 1)
else:
    # Fall back: drop it just before </configuration>
    content = content.replace('</configuration>', '<runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">' + redirects + '</assemblyBinding></runtime></configuration>', 1)
with open(p, 'w', encoding='utf-8') as f: f.write(content)
print(f"OK: {p}")
PYEOF
}

log "patching Ssms.exe.config files..."
inject_redirects_into "$CFG_IDE"
inject_redirects_into "$CFG_APPDATA" || true  # may not exist until SSMS runs once

# -----------------------------------------------------------------
# apply binary patches (GIF→PNG and NavigationService no-op)
# -----------------------------------------------------------------
log "applying GIF→PNG patch (this takes ~1-2 min)..."
"$PATCHER" patch-gifs "$IDE_DIR"

log "applying NavigationService no-op patch..."
"$PATCHER" patch-nav "$IDE_DIR"

# -----------------------------------------------------------------
# enable OE output-window diagnostics (optional but useful)
# -----------------------------------------------------------------
USER_SETTINGS="$WINEPREFIX/drive_c/users/$(whoami)/AppData/Roaming/Microsoft/SQL Server Management Studio/20.0/UserSettings.xml"
if [ -f "$USER_SETTINGS" ] && ! grep -q '<ObjectExplorer>true' "$USER_SETTINGS"; then
    log "enabling OE error output window..."
    # This file is created on first SSMS run; if it doesn't exist, no-op
fi

# -----------------------------------------------------------------
# desktop launcher
# -----------------------------------------------------------------
DESKTOP_FILE="$HOME/.local/share/applications/ssms-on-wine.desktop"
mkdir -p "$(dirname "$DESKTOP_FILE")"
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Name=SQL Server Management Studio 20 (Wine)
Comment=Remember to run 'kinit' before launching if using Kerberos
Exec=env WINEPREFIX=$WINEPREFIX wine "C:\\\\Program Files (x86)\\\\Microsoft SQL Server Management Studio 20\\\\Common7\\\\IDE\\\\Ssms.exe"
Type=Application
Terminal=false
Icon=applications-database
Categories=Development;Database;
StartupWMClass=ssms.exe
EOF
log "desktop launcher written to $DESKTOP_FILE"

# -----------------------------------------------------------------
# done
# -----------------------------------------------------------------
cat <<'EOF'

=====================================================================
  SSMS 20 setup complete.
=====================================================================

Before launching SSMS, get a Kerberos ticket on the HOST (Linux side):

    kinit your.username@YOURREALM.EXAMPLE.COM

Then launch SSMS either from:
  - the app menu entry "SQL Server Management Studio 20 (Wine)"
  - or manually:
      WINEPREFIX=$WINEPREFIX wine \
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe"

Connect using Windows Authentication. SSMS picks up your ticket
via Wine's SSPI stack.

Troubleshooting:
  - If Object Explorer shows no databases, verify NavigationService
    was patched:   $SCRIPT_DIR/bin/ssms-patcher verify "$IDE_DIR"
  - If dialogs throw "Parameter is not valid. (System.Drawing)",
    re-run:        $SCRIPT_DIR/bin/ssms-patcher patch-gifs "$IDE_DIR"
  - To revert all patches:
                   $SCRIPT_DIR/bin/ssms-patcher restore "$IDE_DIR"
  - Error output:  View → Output → select "Object Explorer"

=====================================================================
EOF
