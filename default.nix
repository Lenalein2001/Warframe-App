{ pkgs ? import <nixpkgs> { } }:
let runtime = import ./nix/runtime.nix { inherit pkgs; };
in pkgs.buildDotnetModule {
  pname = "warframe-companion";
  version = "0.1.0";
  src = pkgs.lib.cleanSourceWith {
    src = ./.;
    filter = path: _type: !(builtins.elem (baseNameOf path) [ ".git" "artifacts" "bin" "obj" "build" "result" ]);
  };
  projectFile = "companion/Desktop/Desktop.csproj";
  nugetDeps = ./nix/deps.json;
  dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
  dotnet-runtime = pkgs.dotnetCorePackages.aspnetcore_10_0;
  useAppHost = false;
  executables = [ "WarframeCompanion.Desktop" ];
  dotnetFlags = [ "-p:RuntimeIdentifiers=linux-x64" ];
  nativeBuildInputs = with pkgs; [ cmake pkg-config wrapGAppsHook3 ];
  buildInputs = runtime.libraries ++ [ pkgs.gsettings-desktop-schemas ];
  runtimeDeps = runtime.libraries;
  dontUseCmakeConfigure = true;
  preBuild = ''
    cmake -S companion/Native -B companion/Native/build -DCMAKE_BUILD_TYPE=Release
    cmake --build companion/Native/build --parallel "$NIX_BUILD_CORES"
    ctest --test-dir companion/Native/build --output-on-failure
  '';
  preFixup = ''
    gappsWrapperArgs+=(--prefix PATH : ${pkgs.lib.makeBinPath runtime.tools})
  '';
  postInstall = ''
    install -Dm644 warframeproject.desktop $out/share/applications/warframe-companion.desktop
    substituteInPlace $out/share/applications/warframe-companion.desktop --replace-fail "Exec=/path/to/warframeproject/run.sh" "Exec=WarframeCompanion.Desktop"
  '';
  meta = {
    description = "Warframe companion with C# services and native C/C++ processing";
    mainProgram = "WarframeCompanion.Desktop";
    license = pkgs.lib.licenses.mit;
    platforms = [ "x86_64-linux" ];
  };
}
