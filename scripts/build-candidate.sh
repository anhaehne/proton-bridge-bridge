#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
output_directory=${1:-"$repo_root/artifacts"}
project_path="$repo_root/src/ProtonBridgeBridge/ProtonBridgeBridge.csproj"

if [[ -n $(git -C "$repo_root" status --porcelain) ]]; then
    echo "Candidate packaging requires a clean working tree." >&2
    exit 1
fi

if [[ -z ${PAPERLESS_TEST_PYTHON:-} ]]; then
    echo "Set PAPERLESS_TEST_PYTHON to Python with tests/requirements.txt installed; candidate checks must not skip this test." >&2
    exit 1
fi

version=$(dotnet msbuild "$project_path" -nologo -getProperty:Version)
source_revision=$(git -C "$repo_root" rev-parse HEAD)
source_epoch=$(git -C "$repo_root" show -s --format=%ct HEAD)
image_name="proton-bridge-bridge:$version"
staging_directory=$(mktemp -d /tmp/ProtonBridgeBridge-release.XXXXXX)
trap 'rm -rf -- "$staging_directory"' EXIT
mkdir -p "$output_directory" "$staging_directory/ProtonBridgeBridge"

# No publishing credentials are needed: the outputs remain local review artifacts.
dotnet restore "$repo_root/ProtonBridgeBridge.slnx" --locked-mode
dotnet format "$repo_root/ProtonBridgeBridge.slnx" --no-restore --verify-no-changes
dotnet format style "$repo_root/ProtonBridgeBridge.slnx" --no-restore --verify-no-changes --diagnostics IDE0011
dotnet test "$repo_root/ProtonBridgeBridge.slnx" -c Release --no-restore
dotnet publish "$project_path" -c Release --no-restore \
    -p:ContinuousIntegrationBuild=true -p:SourceRevisionId="$source_revision" \
    -o "$staging_directory/ProtonBridgeBridge" /p:UseAppHost=false
cp "$repo_root/LICENSE" "$repo_root/THIRD-PARTY-NOTICES.txt" "$staging_directory/ProtonBridgeBridge/"
cp "$repo_root/deploy/config.example.json" "$staging_directory/ProtonBridgeBridge/config.example.json"
mkdir -p "$staging_directory/ProtonBridgeBridge/deploy"
sed '/^    build:/,+1d' "$repo_root/deploy/compose.yaml" > "$staging_directory/ProtonBridgeBridge/deploy/compose.yaml"
cp "$repo_root/tests/fixtures/controlled-test.eml" "$staging_directory/ProtonBridgeBridge/controlled-test.eml"
cp "$repo_root/docs/first-production-test.md" "$staging_directory/ProtonBridgeBridge/TESTING.md"

dotnet list "$repo_root/ProtonBridgeBridge.slnx" package --vulnerable --include-transitive --format json \
    > "$output_directory/ProtonBridgeBridge-$version-dependency-audit.json"
tar --sort=name --mtime="@$source_epoch" --owner=0 --group=0 --numeric-owner \
    -C "$staging_directory" -czf "$output_directory/ProtonBridgeBridge-$version-portable.tar.gz" ProtonBridgeBridge

docker build --build-arg SOURCE_REVISION="$source_revision" -t "$image_name" "$repo_root"
bash "$repo_root/scripts/smoke-container.sh" "$image_name"
docker save "$image_name" | gzip -n > "$output_directory/ProtonBridgeBridge-$version-image.tar.gz"
image_id=$(docker image inspect "$image_name" --format '{{.Id}}')
printf '{"Version":"%s","SourceRevision":"%s","LocalImageId":"%s","LiveBridgeValidated":false}\n' \
    "$version" "$source_revision" "$image_id" > "$output_directory/ProtonBridgeBridge-$version-build.json"

(
    cd -- "$output_directory"
    sha256sum "ProtonBridgeBridge-$version-portable.tar.gz" "ProtonBridgeBridge-$version-image.tar.gz" \
        "ProtonBridgeBridge-$version-build.json" "ProtonBridgeBridge-$version-dependency-audit.json" \
        > "ProtonBridgeBridge-$version.sha256"
)
echo "Candidate $version packaged in $output_directory; nothing was published or deployed."
