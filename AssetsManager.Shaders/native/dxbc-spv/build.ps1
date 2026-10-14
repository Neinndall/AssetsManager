param(
    [string]$ZigVersion = "0.15.2"
)

$ErrorActionPreference = "Stop"

$DxbcCommit = "bf14419e5fa7eacb817b7b632f03cb61d61bbad7"
$SpirvHeadersCommit = "c8ad050fcb29e42a2f57d9f59e97488f465c436d"
$Work = Join-Path $env:TEMP "assetsmanager-dxbc-spv-build"
$DxbcArchive = Join-Path $Work "dxbc.zip"
$SpirvArchive = Join-Path $Work "spirv.zip"
$ZigArchive = Join-Path $Work "zig.zip"
$Dxbc = Join-Path $Work "dxbc-spirv-$DxbcCommit"
$Spirv = Join-Path $Work "SPIRV-Headers-$SpirvHeadersCommit"
$Zig = Join-Path $Work "zig-x86_64-windows-$ZigVersion\zig.exe"
$Output = Join-Path $PSScriptRoot "..\..\runtimes\win-x64\native\dxbc_spv_lol.dll"

if (Test-Path $Work) {
    Remove-Item $Work -Recurse -Force
}
New-Item -ItemType Directory -Path $Work | Out-Null

Invoke-WebRequest "https://codeload.github.com/doitsujin/dxbc-spirv/zip/$DxbcCommit" -OutFile $DxbcArchive
Invoke-WebRequest "https://codeload.github.com/KhronosGroup/SPIRV-Headers/zip/$SpirvHeadersCommit" -OutFile $SpirvArchive
Invoke-WebRequest "https://ziglang.org/download/$ZigVersion/zig-x86_64-windows-$ZigVersion.zip" -OutFile $ZigArchive
Expand-Archive $DxbcArchive -DestinationPath $Work
Expand-Archive $SpirvArchive -DestinationPath $Work
Expand-Archive $ZigArchive -DestinationPath $Work

$Submodule = Join-Path $Dxbc "submodules\spirv_headers"
New-Item -ItemType Directory -Path (Split-Path $Submodule) -Force | Out-Null
Copy-Item $Spirv $Submodule -Recurse

$Sources = @(
    "dxbc/dxbc_api.cpp",
    "dxbc/dxbc_container.cpp",
    "dxbc/dxbc_converter.cpp",
    "dxbc/dxbc_disasm.cpp",
    "dxbc/dxbc_interface.cpp",
    "dxbc/dxbc_io_map.cpp",
    "dxbc/dxbc_parser.cpp",
    "dxbc/dxbc_registers.cpp",
    "dxbc/dxbc_resources.cpp",
    "dxbc/dxbc_signature.cpp",
    "dxbc/dxbc_types.cpp",
    "spirv/spirv_builder.cpp",
    "spirv/spirv_mapping.cpp",
    "ir/ir.cpp",
    "ir/ir_builder.cpp",
    "ir/ir_disasm.cpp",
    "ir/ir_divergence.cpp",
    "ir/ir_dominance.cpp",
    "ir/ir_legalize.cpp",
    "ir/ir_serialize.cpp",
    "ir/ir_utils.cpp",
    "ir/passes/ir_pass_arithmetic.cpp",
    "ir/passes/ir_pass_buffer_kind.cpp",
    "ir/passes/ir_pass_cfg_cleanup.cpp",
    "ir/passes/ir_pass_cfg_convert.cpp",
    "ir/passes/ir_pass_cse.cpp",
    "ir/passes/ir_pass_derivative.cpp",
    "ir/passes/ir_pass_descriptor_indexing.cpp",
    "ir/passes/ir_pass_function.cpp",
    "ir/passes/ir_pass_lower_consume.cpp",
    "ir/passes/ir_pass_lower_io.cpp",
    "ir/passes/ir_pass_lower_min16.cpp",
    "ir/passes/ir_pass_propagate_resource_types.cpp",
    "ir/passes/ir_pass_propagate_types.cpp",
    "ir/passes/ir_pass_remove_unused.cpp",
    "ir/passes/ir_pass_scalarize.cpp",
    "ir/passes/ir_pass_scratch.cpp",
    "ir/passes/ir_pass_ssa.cpp",
    "ir/passes/ir_pass_sync.cpp",
    "util/util_float16.cpp",
    "util/util_log.cpp",
    "util/util_md5.cpp",
    "util/util_swizzle.cpp"
)

$Arguments = @(
    "c++",
    "-shared",
    "-std=c++17",
    "-O2",
    "-DNDEBUG",
    "-DDXBC_SPV_ENABLE_SM5",
    "-DDXBC_SPV_ENABLE_SPIRV",
    "-I$Dxbc"
)
$Arguments += $Sources | ForEach-Object { Join-Path $Dxbc $_ }
$Arguments += @(
    (Join-Path $PSScriptRoot "shim.cpp"),
    "-o",
    $Output
)

New-Item -ItemType Directory -Path (Split-Path $Output) -Force | Out-Null
& $Zig @Arguments
if ($LASTEXITCODE -ne 0) {
    throw "dxbc-spirv native build failed with exit code $LASTEXITCODE."
}

Write-Output "Built $Output"
