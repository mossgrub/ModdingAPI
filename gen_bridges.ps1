$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$projectRoot = 'D:\Unity\Projects\Hollow Knight (Legacy)'

$cecilPath = Join-Path $projectRoot 'Assets\Plugins\Mono.Cecil.dll'
$mmhookPaths = @(
    (Join-Path $projectRoot 'Assets\Plugins\MMHOOK_Assembly-CSharp.dll'),
    (Join-Path $projectRoot 'Assets\Plugins\MMHOOK_PlayMaker.dll')
)
$outPath = Join-Path $projectRoot 'Assets\Scripts\Assembly-CSharp\Modding\GeneratedBridges.cs'
$extraSigsFile = Join-Path $projectRoot 'Assets\Scripts\Assembly-CSharp\Modding\gen_extra_signatures.txt'

if (Test-Path $cecilPath) {
    $cecil = [System.Reflection.Assembly]::LoadFrom($cecilPath)
}

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory((Join-Path $projectRoot 'Assets\Plugins'))
if (Test-Path (Join-Path $projectRoot 'Library\ScriptAssemblies')) {
    $resolver.AddSearchDirectory((Join-Path $projectRoot 'Library\ScriptAssemblies'))
}
$readerParams = New-Object Mono.Cecil.ReaderParameters
$readerParams.AssemblyResolver = $resolver

function Split-GenericArgs([string]$s) {
    $result = New-Object System.Collections.Generic.List[string]
    $depth = 0; $cur = ''
    foreach ($ch in $s.ToCharArray()) {
        if ($ch -eq '<' -or $ch -eq '[') { $depth++; $cur += $ch; continue }
        if ($ch -eq '>' -or $ch -eq ']') { $depth--; $cur += $ch; continue }
        if ($ch -eq ',' -and $depth -eq 0) { $result.Add($cur.Trim()); $cur = ''; continue }
        $cur += $ch
    }
    if ($cur.Trim() -ne '') { $result.Add($cur.Trim()) }
    return $result
}

function Convert-Type([string]$fn) {
    $arr = ''
    while ($fn.EndsWith('[]')) { $arr += '[]'; $fn = $fn.Substring(0, $fn.Length - 2) }
    $bt = $fn.IndexOf('`')
    if ($bt -ge 0) {
        $base = $fn.Substring(0, $bt) -replace '/', '.'
        $ob = $fn.IndexOf('[', $bt)
        if ($ob -lt 0) { $ob = $fn.IndexOf('<', $bt) }
        if ($ob -ge 0) {
            $close = if ($fn[$ob] -eq '[') { $fn.LastIndexOf(']') } else { $fn.LastIndexOf('>') }
            if ($close -gt $ob) {
                $inner = $fn.Substring($ob + 1, $close - $ob - 1)
                $converted = @()
                foreach ($a in (Split-GenericArgs $inner)) { $converted += (Convert-Type $a) }
                return "$base<$($converted -join ',')>$arr"
            }
        }
        return "$base$arr"
    }
    $simple = $fn -replace '/', '.'
    $mapped = switch ($simple) {
        'System.Void'    { 'void' }
        'System.Boolean' { 'bool' }
        'System.Int32'   { 'int' }
        'System.Single'  { 'float' }
        'System.String'  { 'string' }
        'System.Object'  { 'object' }
        'System.Int64'   { 'long' }
        'System.UInt32'  { 'uint' }
        'System.Double'  { 'double' }
        'System.Int16'   { 'short' }
        'System.Byte'    { 'byte' }
        'System.Char'    { 'char' }
        'System.Decimal' { 'decimal' }
        'System.SByte'   { 'sbyte' }
        'System.UInt64'  { 'ulong' }
        'System.UInt16'  { 'ushort' }
        'System.IntPtr'  { 'IntPtr' }
        'System.UIntPtr' { 'UIntPtr' }
        default          { $simple }
    }
    return "$mapped$arr"
}

function Normalize-RawType([string]$t) {
    $normalized = $t -replace '/', '+'
    switch ($normalized) {
        'void'    { return 'System.Void' }
        'bool'    { return 'System.Boolean' }
        'int'     { return 'System.Int32' }
        'float'   { return 'System.Single' }
        'string'  { return 'System.String' }
        'object'  { return 'System.Object' }
        'long'    { return 'System.Int64' }
        'uint'    { return 'System.UInt32' }
        'double'  { return 'System.Double' }
        'short'   { return 'System.Int16' }
        'byte'    { return 'System.Byte' }
        'char'    { return 'System.Char' }
        default   { return $normalized }
    }
}

function Is-RefTypeString([string]$typeStr) {
    if ($typeStr -eq 'void' -or $typeStr -eq 'System.Void') {
        return $false
    }
    $valueTypes = @(
        'bool', 'System.Boolean',
        'int', 'System.Int32',
        'float', 'System.Single',
        'double', 'System.Double',
        'long', 'System.Int64',
        'uint', 'System.UInt32',
        'short', 'System.Int16',
        'byte', 'System.Byte',
        'char', 'System.Char',
        'sbyte', 'System.SByte',
        'ulong', 'System.UInt64',
        'ushort', 'System.UInt16',
        'decimal', 'System.Decimal',
        'System.IntPtr', 'IntPtr',
        'System.UIntPtr', 'UIntPtr'
    )
    if ($valueTypes -contains $typeStr) { return $false }
    return $true
}

function Has-UnboundGeneric($td) {
    if ($null -eq $td) { return $false }
    if ($td.IsGenericInstance) {
        foreach ($ga in $td.GenericArguments) {
            if (Has-UnboundGeneric $ga) { return $true }
        }
        return $false
    }
    if ($td.IsGenericParameter) { return $true }
    if ($td.HasGenericParameters) { return $true }
    if ($td.IsByReference) { return $true }
    if ($td.IsArray) {
        return Has-UnboundGeneric $td.ElementType
    }
    return $false
}

function Is-AssemblyType($td) {
    if ($null -eq $td) { return $false }
    return $td.FullName -eq 'System.Reflection.Assembly'
}

function Is-SafeBlittableType($pt) {
    if ($null -eq $pt) { return $false }
    if (Has-UnboundGeneric $pt) { return $false }
    return $true
}

$sigCounts = @{}
$sigData = @{}
$skippedByref = 0
$skippedArity = 0
$skippedUnbound = 0
$skippedAssembly = 0
$skippedComplexStruct = 0
$total = 0
$extraSeq = 0

if (Test-Path $extraSigsFile) {
    foreach ($line in [System.IO.File]::ReadAllLines($extraSigsFile)) {
        $l = $line.Trim()
        if ($l -eq '' -or $l.StartsWith('#')) { continue }
        $parts = $l -split '\|', 2
        if ($parts.Count -ne 2) { continue }
        $retCs = $parts[0].Trim()
        $paramCs = @()
        $rawParams = @()
        $nativeParamCs = @()
        $okParams = $true
        if ($parts[1].Trim() -ne '') {
            foreach ($p in (Split-GenericArgs $parts[1])) {
                $pcs = $p.Trim()
                if ($pcs -eq '') { continue }
                if ($pcs -match '`|<>') { $skippedUnbound++; $okParams = $false; break }
                $paramCs += $pcs
                $rawParams += (Normalize-RawType $pcs)
                if (Is-RefTypeString $pcs) { $nativeParamCs += 'IntPtr' } else { $nativeParamCs += $pcs }
            }
        }
        if (-not $okParams) { continue }
        if ($retCs -match '`|<>') { $skippedUnbound++; continue }
        
        $nativeRetCs = if (Is-RefTypeString $retCs) { 'IntPtr' } else { $retCs }

        $sig = "$retCs|$($paramCs -join ',')"
        if ($sigCounts.ContainsKey($sig)) { $sigCounts[$sig]++ }
        else {
            $sigCounts[$sig] = 1
            $sigData[$sig] = @{
                Ret = $retCs
                NativeRet = $nativeRetCs
                Params = @($paramCs)
                NativeParams = @($nativeParamCs)
                RawParams = @($rawParams)
            }
            $extraSeq++
        }
    }
}

foreach ($path in $mmhookPaths) {
    if (-not (Test-Path $path)) { 
        Write-Warning "MMHOOK missing: $path"
        continue 
    }
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, $readerParams)
    
    foreach ($type in $asm.MainModule.Types) {
        if (-not $type.FullName.StartsWith('On.')) { continue }
        foreach ($nested in $type.NestedTypes) {
            if (-not $nested.Name.StartsWith('orig_')) { continue }
            $invoke = $nested.Methods | Where-Object { $_.Name -eq 'Invoke' }
            if ($null -eq $invoke) { continue }
            $total++
            
            $paramCs = @()
            $nativeParamCs = @()
            $rawParams = @()
            $ok = $true
            foreach ($p in $invoke.Parameters) {
                $pt = $p.ParameterType
                if ($pt.IsByReference) { $skippedByref++; $ok = $false; break }
                if (Has-UnboundGeneric $pt) { $skippedUnbound++; $ok = $false; break }
                if (Is-AssemblyType $pt) { $skippedAssembly++; $ok = $false; break }
                if (-not (Is-SafeBlittableType $pt)) { $skippedComplexStruct++; $ok = $false; break }
                
                $csType = Convert-Type $pt.FullName
                $paramCs += $csType
                $rawParams += (Normalize-RawType $pt.FullName)
                
                $isRef = (-not $pt.IsValueType)
                if ($isRef) { $nativeParamCs += 'IntPtr' } else { $nativeParamCs += $csType }
            }
            if (-not $ok) { continue }
            if (Is-AssemblyType $invoke.ReturnType) { $skippedAssembly++; continue }
            if (Has-UnboundGeneric $invoke.ReturnType) { $skippedUnbound++; continue }
            if ($invoke.ReturnType.FullName -ne 'System.Void' -and $invoke.ReturnType.FullName -ne 'void') {
                if (-not (Is-SafeBlittableType $invoke.ReturnType)) { $skippedComplexStruct++; continue }
            }
            if ($paramCs.Count -gt 6) { $skippedArity++; continue }
            
            $retPt = $invoke.ReturnType
            $retCs = Convert-Type $retPt.FullName
            if (($retCs + ',' + ($paramCs -join ',')) -match '`|<>') { $skippedUnbound++; continue }
            
            $isRetRef = ($retCs -ne 'void') -and (-not $retPt.IsValueType)
            $nativeRetCs = if ($isRetRef) { 'IntPtr' } else { $retCs }
            
            $sig = "$retCs|$($paramCs -join ',')"
            if ($sigCounts.ContainsKey($sig)) {
                $sigCounts[$sig]++
            } else {
                $sigCounts[$sig] = 1
                $sigData[$sig] = @{
                    Ret = $retCs
                    NativeRet = $nativeRetCs
                    Params = $paramCs
                    NativeParams = $nativeParamCs
                    RawParams = $rawParams
                }
            }
        }
    }
}
$sorted = $sigCounts.GetEnumerator() | Sort-Object Value -Descending
$allEntries = @($sorted)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// Auto-Generated - gen_bridges.ps1')
[void]$sb.AppendLine('using System;')
[void]$sb.AppendLine('using System.Reflection;')
[void]$sb.AppendLine('using System.Runtime.InteropServices;')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('namespace Modding')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    internal static class GeneratedBridges')
[void]$sb.AppendLine('    {')

$maxBridgesPerChunk = 500
$totalBridges = $allEntries.Count

[void]$sb.AppendLine('        internal static void RegisterAll()')
[void]$sb.AppendLine('        {')

$origSeq = 0
$origSigs = @{}

$totalChunks = [Math]::Ceiling($totalBridges / $maxBridgesPerChunk)
if ($totalChunks -lt 1) { $totalChunks = 1 }

for ($c = 0; $c -lt $totalChunks; $c++) {
    [void]$sb.AppendLine("            GeneratedBridgesPart$c.RegisterAll();")
}
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('    }')

$globalSlotIndex = 0
$bridgeCount = 0

for ($chunkIndex = 0; $chunkIndex -lt $totalChunks; $chunkIndex++) {
    $startIndex = $chunkIndex * $maxBridgesPerChunk
    $endIndex = [Math]::Min(($startIndex + $maxBridgesPerChunk - 1), ($totalBridges - 1))
    
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("    internal static class GeneratedBridgesPart$chunkIndex")
    [void]$sb.AppendLine('    {')
    
    $chunkRegisterSb = New-Object System.Text.StringBuilder
    [void]$chunkRegisterSb.AppendLine('        internal static void RegisterAll()')
    [void]$chunkRegisterSb.AppendLine('        {')

    for ($i = $startIndex; $i -le $endIndex; $i++) {
        if ($i -ge $allEntries.Count) { break }
        $e = $allEntries[$i]
        $sig = $e.Key
        $freq = $e.Value
        $d = $sigData[$sig]
        $ret = $d.Ret
        $nativeRet = $d.NativeRet
        $params = $d.Params
        $nativeParams = $d.NativeParams
        $rawParams = $d.RawParams
        $arity = $params.Count

        if (-not $origSigs.ContainsKey($sig)) {
            $oname = "OrigSig" + $origSeq
            $origSigs[$sig] = $oname
            $origSeq++
            $opd = @()
            for ($oi = 0; $oi -lt $arity; $oi++) { $opd += "$($nativeParams[$oi]) o$oi" }
            $oargs = $opd -join ', '
            [void]$sb.AppendLine("        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]")
            if ($nativeRet -eq 'void') {
                [void]$sb.AppendLine("        public delegate void $oname($oargs);")
            } else {
                [void]$sb.AppendLine("        public delegate $nativeRet $oname($oargs);")
            }
        } else {
            $oname = $origSigs[$sig]
        }

        $slots = 2
        if ($freq -ge 50) { $slots = 8 }
        elseif ($freq -ge 20) { $slots = 6 }
        elseif ($freq -ge 8) { $slots = 4 }
        elseif ($freq -ge 3) { $slots = 3 }

        $rawParamArr = if ($arity -eq 0) {
            "Array.Empty<string>()"
        } else {
            $formatted = ($rawParams | ForEach-Object { "`"$_`"" }) -join ', '
            "new string[] { $formatted }"
        }

        for ($s = 0; $s -lt $slots; $s++) {
            $slotName = "GenSlot$globalSlotIndex"
            $bridgeName = "GenBridge$globalSlotIndex"
            $globalSlotIndex++

            if ($nativeRet -eq 'void') {
                if ($arity -eq 0) { $delType = 'DetourBridge.DetourAction' }
                else { $delType = "DetourBridge.DetourAction<$($nativeParams -join ', ')>" }
            } else {
                if ($arity -eq 0) { $delType = "DetourBridge.DetourFunc<$nativeRet>" }
                else { $delType = "DetourBridge.DetourFunc<$($nativeParams -join ', '), $nativeRet>" }
            }

            $sigParamDecl = @()
            $args = @()
            for ($k = 0; $k -lt $arity; $k++) {
                $sigParamDecl += "$($nativeParams[$k]) a$k"
                $args += "a$k"
            }
            $argList = $args -join ', '
            $objArray = if ($arity -eq 0) { "Array.Empty<object>()" } else { "new object[] { $argList }" }

            [void]$sb.AppendLine("        private sealed class $slotName { }")
            if ($nativeRet -eq 'void') {
                [void]$sb.AppendLine("        [AOT.MonoPInvokeCallback(typeof($delType))]")
                [void]$sb.AppendLine("        private static void $bridgeName($($sigParamDecl -join ', '))")
                [void]$sb.AppendLine("        {")
                [void]$sb.AppendLine("            DetourBridge.InvokeBridge<$slotName>($objArray);")
                [void]$sb.AppendLine("        }")
            } else {
                [void]$sb.AppendLine("        [AOT.MonoPInvokeCallback(typeof($delType))]")
                [void]$sb.AppendLine("        private static $nativeRet $bridgeName($($sigParamDecl -join ', '))")
                [void]$sb.AppendLine("        {")
                [void]$sb.AppendLine("            return DetourBridge.InvokeBridgeR<$nativeRet, $slotName>($objArray);")
                [void]$sb.AppendLine("        }")
            }

            [void]$chunkRegisterSb.AppendLine("            DetourBridge.RegisterGeneratedBridge(typeof($delType), typeof($slotName), typeof(GeneratedBridgesPart$chunkIndex).GetMethod(nameof($bridgeName), BindingFlags.NonPublic | BindingFlags.Static), typeof($oname), $rawParamArr);")

            $bridgeCount++
        }
    }

    [void]$chunkRegisterSb.AppendLine('        }')
    [void]$sb.Append($chunkRegisterSb.ToString())
    [void]$sb.AppendLine('    }')
}

[void]$sb.AppendLine('}')

[System.IO.File]::WriteAllText($outPath, $sb.ToString(), [System.Text.Encoding]::UTF8)
Write-Host "Total=$total byref=$skippedByref arityGt6=$skippedArity skippedUnbound=$skippedUnbound skippedAssembly=$skippedAssembly skippedComplexStruct=$skippedComplexStruct extras=$extraSeq distinct=$($sigCounts.Count) bridges=$bridgeCount"