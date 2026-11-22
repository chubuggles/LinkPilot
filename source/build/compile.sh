#!/bin/sh
# Compiles LinkPilot against the .NET Framework 4.8 reference assemblies using the Roslyn compiler.
set -e
HERE=$(cd "$(dirname "$0")/.." && pwd)
REF=/usr/lib/mono/4.8-api
CSC="dotnet /usr/lib/dotnet/sdk/8.0.134/Roslyn/bincore/csc.dll"
COMMON="-nologo -noconfig -nostdlib+ -langversion:latest -nullable:enable -optimize+ -warn:4 -nowarn:CS8632
 -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Runtime.Serialization.dll
 -r:$REF/System.Xml.dll"
CORE=$(find "$HERE/src/Hid" "$HERE/src/Link" "$HERE/src/Core" -name '*.cs')
mkdir -p "$HERE/out"
case "$1" in
  tests)
    $CSC $COMMON -target:exe -out:"$HERE/out/ProtocolTests.exe" $CORE "$HERE/tests/ProtocolTests.cs" ;;
  *)
    $CSC $COMMON -target:winexe -platform:anycpu -win32manifest:"$HERE/src/app.manifest" -win32icon:"$HERE/src/App/LinkPilot.ico" \
      -r:$REF/System.Windows.Forms.dll -r:$REF/System.Drawing.dll \
      -out:"$HERE/out/LinkPilot.exe" $CORE $(find "$HERE/src/App" -name '*.cs') ;;
esac
