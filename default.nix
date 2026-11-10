{ pkgs ? import <nixpkgs> { } }:
let runtime = import ./nix/runtime.nix { inherit pkgs; };
in pkgs.buildDotnetModule {
  pname = "warframe-companion";
  version = "0.1.0";
  src = pkgs.lib.cleanSourceWith {
    src = ./.;
    filter = path: _type: !(builtins.elem (baseNameOf path) [ ".git" "artifacts" "bin" "obj" "build" "result" ]);
  };
  projectFile = "src/Desktop/Desktop.csproj";
  nugetDeps = ./nix/deps.json;
  dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
  dotnet-runtime = pkgs.dotnetCorePackages.aspnetcore_10_0;
  useAppHost = false;
  executables = [ "WarframeCompanion.Desktop" ];
  dotnetFlags = [ "-p:RuntimeIdentifiers=linux-x64" ];
  nativeBuildInputs = with pkgs; [ gcc cmake pkg-config wrapGAppsHook3 ];
  buildInputs = runtime.libraries ++ [ pkgs.gsettings-desktop-schemas ];
  runtimeDeps = runtime.libraries;
  dontUseCmakeConfigure = true;
  preBuild = ''
    cmake -S src/Native -B src/Native/build -DCMAKE_BUILD_TYPE=Release
    cmake --build src/Native/build --parallel "$NIX_BUILD_CORES"
    ctest --test-dir src/Native/build --output-on-failure
  '';
  preFixup = ''
    gappsWrapperArgs+=(--prefix PATH : ${pkgs.lib.makeBinPath runtime.tools})
  '';
  postInstall = ''
    install -Dm644 packaging/linux/warframe-companion.desktop $out/share/applications/warframe-companion.desktop
  '';
  meta = {
    description = "Warframe companion with C# services and native C/C++ processing";
    mainProgram = "WarframeCompanion.Desktop";
    license = pkgs.lib.licenses.mit;
    platforms = [ "x86_64-linux" ];
  };
}
