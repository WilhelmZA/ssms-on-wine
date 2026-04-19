.PHONY: all build clean run-verify help

BIN      := bin/ssms-patcher
IDE_DIR  ?= $(HOME)/.wine-ssms/drive_c/Program Files (x86)/Microsoft SQL Server Management Studio 20/Common7/IDE

all: build

help:
	@echo "targets:"
	@echo "  make build       — compile ssms-patcher into ./bin/ (requires dotnet SDK 10+)"
	@echo "  make clean       — remove build artefacts"
	@echo "  make run-verify  — run ssms-patcher verify against an installed SSMS"
	@echo ""
	@echo "release-oriented:"
	@echo "  make build is what the GitHub Release job runs. You can run it locally"
	@echo "  to get a self-contained ELF at ./bin/ssms-patcher (~73 MB, ships the"
	@echo "  .NET 10 runtime embedded)."

build: $(BIN)

$(BIN): src/Program.cs src/ssms-patcher.csproj
	@command -v dotnet >/dev/null 2>&1 || { \
	  echo "error: dotnet SDK not found. Install dotnet-sdk-10.0 first."; exit 1; }
	@mkdir -p bin
	cd src && dotnet publish -c Release -r linux-x64 --self-contained -o ../bin
	@# keep only the native ELF + remove pdb
	@rm -f bin/*.pdb bin/ssms-patcher.dll
	@echo ""
	@echo "built: $(BIN)  ($$(stat -c%s $(BIN) | numfmt --to=iec))"

clean:
	rm -rf bin/ssms-patcher bin/*.pdb src/bin src/obj

run-verify:
	@test -x $(BIN) || { echo "run 'make build' first"; exit 1; }
	$(BIN) verify "$(IDE_DIR)"
