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
    switch ($t) {
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
        default   { return $t }
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
        'System.UIntPtr', 'UIntPtr',
        'UnityEngine.Vector2', 'UnityEngine.Vector3', 'UnityEngine.Vector4',
        'UnityEngine.Quaternion', 'UnityEngine.Color', 'UnityEngine.Color32',
        'UnityEngine.Rect', 'UnityEngine.Bounds', 'UnityEngine.Matrix4x4',
        'GlobalEnums.CollisionSide', 'HitInstance', 'DieCause', 'AttackTypes'
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

$sigCounts = @{}
$sigData = @{}
$skippedByref = 0
$skippedArity = 0
$skippedUnbound = 0
$skippedAssembly = 0
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
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    
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
                
                $csType = Convert-Type $pt.FullName
                $paramCs += $csType
                $rawParams += $pt.FullName
                
                $isRef = (-not $pt.IsValueType)
                if ($isRef) { $nativeParamCs += 'IntPtr' } else { $nativeParamCs += $csType }
            }
            if (-not $ok) { continue }
            if (Is-AssemblyType $invoke.ReturnType) { $skippedAssembly++; continue }
            if (Has-UnboundGeneric $invoke.ReturnType) { $skippedUnbound++; continue }
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

$bridgeCount = 0
$slotIndex = 0
$origSeq = 0
$origSigs = @{}
foreach ($e in $sorted) {
    $sig = $e.Key
    $freq = $e.Value
    $d = $sigData[$sig]
    $ret = $d.Ret
    $nativeRet = $d.NativeRet
    $params = $d.Params
    $nativeParams = $d.NativeParams
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
    }

    $slots = 1
    if ($freq -ge 50) { $slots = 6 }
    elseif ($freq -ge 20) { $slots = 4 }
    elseif ($freq -ge 8) { $slots = 3 }
    elseif ($freq -ge 3) { $slots = 2 }

    for ($s = 0; $s -lt $slots; $s++) {
        $slotName = "GenSlot$slotIndex"
        $bridgeName = "GenBridge$slotIndex"
        $slotIndex++

        if ($nativeRet -eq 'void') {
            if ($arity -eq 0) { $delType = 'DetourBridge.DetourAction' }
            else { $delType = "DetourBridge.DetourAction<$($nativeParams -join ', ')>" }
        } else {
            if ($arity -eq 0) { $delType = "DetourBridge.DetourFunc<$nativeRet>" }
            else { $delType = "DetourBridge.DetourFunc<$($nativeParams -join ', '), $nativeRet>" }
        }

        $sigParamDecl = @()
        $args = @()
        for ($i = 0; $i -lt $arity; $i++) {
            $sigParamDecl += "$($nativeParams[$i]) a$i"
            $args += "a$i"
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
        $bridgeCount++
    }
}

$sb2 = New-Object System.Text.StringBuilder
[void]$sb2.AppendLine('        internal static void RegisterAll()')
[void]$sb2.AppendLine('        {')
$slotIndex2 = 0
foreach ($e in $sorted) {
    $sig = $e.Key
    $freq = $e.Value
    $d = $sigData[$sig]
    $ret = $d.Ret
    $nativeRet = $d.NativeRet
    $nativeParams = $d.NativeParams
    $params = $d.Params
    $arity = $params.Count
    $rawParams = $d.RawParams

    $rawParamArr = if ($arity -eq 0) {
        "Array.Empty<string>()"
    } else {
        $formatted = ($rawParams | ForEach-Object { "`"$_`"" }) -join ', '
        "new string[] { $formatted }"
    }

    $slots = 1
    if ($freq -ge 50) { $slots = 6 }
    elseif ($freq -ge 20) { $slots = 4 }
    elseif ($freq -ge 8) { $slots = 3 }
    elseif ($freq -ge 3) { $slots = 2 }
    for ($s = 0; $s -lt $slots; $s++) {
        $slotName = "GenSlot$slotIndex2"
        $bridgeName = "GenBridge$slotIndex2"
        $slotIndex2++
        if ($nativeRet -eq 'void') {
            if ($arity -eq 0) { $delType = 'DetourBridge.DetourAction' }
            else { $delType = "DetourBridge.DetourAction<$($nativeParams -join ', ')>" }
        } else {
            if ($arity -eq 0) { $delType = "DetourBridge.DetourFunc<$nativeRet>" }
            else { $delType = "DetourBridge.DetourFunc<$($nativeParams -join ', '), $nativeRet>" }
        }
        [void]$sb2.AppendLine("            DetourBridge.RegisterGeneratedBridge(typeof($delType), typeof($slotName), typeof(GeneratedBridges).GetMethod(nameof($bridgeName), BindingFlags.NonPublic | BindingFlags.Static), typeof($($origSigs[$sig])), $rawParamArr);")
    }
}
[void]$sb2.AppendLine('        }')

[void]$sb.Append($sb2.ToString())
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

[System.IO.File]::WriteAllText($outPath, $sb.ToString(), [System.Text.Encoding]::UTF8)
Write-Host "Done total=$total byref=$skippedByref arityGt6=$skippedArity skippedUnbound=$skippedUnbound skippedAssembly=$skippedAssembly extras=$extraSeq distinct=$($sigCounts.Count) bridges=$bridgeCount"
Write-Host "File size: $((Get-Item $outPath).Length) bytes"