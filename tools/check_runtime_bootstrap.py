#!/usr/bin/env python3
"""Build isolated .NET load-context regressions from the production bootstrap source."""
import argparse
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--entry-assembly", type=Path, action="append", default=[], help="Also verify the built production entry module contains its initializer.")
    parser.add_argument("--peer-repo", type=Path, action="append", default=[], help="Check source parity with another participating mod checkout.")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    source = root / "tests/runtime-bootstrap"
    bootstrap = root / "Shared/RuntimeBootstrap.cs"
    for peer in args.peer_repo:
        for relative in ["Shared/RuntimeBootstrap.cs", "tests/runtime-bootstrap/Runner.cs", "tests/runtime-bootstrap/Fixture.cs", "tests/runtime-bootstrap/Consumer.cs", "tools/check_runtime_bootstrap.py"]:
            if (root / relative).read_bytes() != (peer / relative).read_bytes():
                raise RuntimeError(f"Shared runtime policy drift: {peer / relative}")
    with tempfile.TemporaryDirectory(prefix="meowsci-runtime-") as folder:
        work = Path(folder)
        (work / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        common = '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors><NuGetAudit>false</NuGetAudit>'
        references = []
        for name in ("GatOSFixture", "UnscienceFixture", "Purr2D2Fixture", "NetworkConsumer"):
            project = work / name
            project.mkdir()
            inputs = [source / "Consumer.cs"] if name == "NetworkConsumer" else [bootstrap, source / "Fixture.cs"]
            includes = ''.join(f'<Compile Include="{escape(str(p), {chr(34): "&quot;"})}" />' for p in inputs)
            (project / f"{name}.csproj").write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>{common}</PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>')
            references.append(f'<ProjectReference Include="{name}/{name}.csproj" />')
        runner = work / "Runner.csproj"
        runner.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>{common}<OutputType>Exe</OutputType></PropertyGroup><ItemGroup><Compile Include="{escape(str(source / "Runner.cs"))}" />{"".join(references)}</ItemGroup></Project>')
        build = subprocess.run(["dotnet", "build", str(runner), "--nologo", "-v:q", "--disable-build-servers", "-m:1"], text=True, capture_output=True, timeout=180)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        command = ["dotnet", str(work / "bin/Debug/net10.0/Runner.dll")]
        for entry in args.entry_assembly:
            command += ["--verify-entry", str(entry.resolve())]
        subprocess.run(command, check=True)


if __name__ == "__main__":
    main()
