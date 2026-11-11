# Development shell: the C# and C++ toolchains, the GTK/WebKit libraries the app links against, and the
# tools it runs (tesseract, grim). `./run.sh` uses it automatically on NixOS.
{ pkgs ? import <nixpkgs> { } }:
let
  runtime = import ./nix/runtime.nix { inherit pkgs; };
in
pkgs.mkShell {
  nativeBuildInputs = with pkgs; [ dotnetCorePackages.sdk_10_0 cmake gcc pkg-config ];
  buildInputs = runtime.libraries;
  packages = runtime.tools;
  shellHook = runtime.environment + ''
    export LD_LIBRARY_PATH="${pkgs.lib.makeLibraryPath runtime.libraries}''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  '';
}
