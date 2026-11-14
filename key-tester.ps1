# key-tester.ps1
# Показывает, что именно шлёт в Windows каждая клавиша твоей клавиатуры.
#
# Зачем: чтобы выбрать, какими клавишами запускать макросы, надо знать,
# видит ли их вообще система. Обычные клавиши видит. А вот служебные /
# макро-клавиши некоторых клавиатур шлют собственные HID-репорты, которых
# в обычном потоке ввода нет — такая клавиша здесь просто не появится.
#
# Как пользоваться:
#   1. Запусти
#   2. Жми по очереди клавиши, которые хочешь отдать под макросы
#   3. Пришли мне скриншот окна или список из него

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace Win32 -Name Map -MemberDefinition @'
[DllImport("user32.dll")]
public static extern uint MapVirtualKey(uint uCode, uint uMapType);
'@

$MAPVK_VK_TO_VSC = 0

$form                 = New-Object System.Windows.Forms.Form
$form.Text            = "Что шлёт клавиша"
$form.Size            = New-Object System.Drawing.Size(560, 620)
$form.StartPosition   = "CenterScreen"
$form.BackColor       = [System.Drawing.Color]::FromArgb(18, 22, 24)
$form.KeyPreview      = $true

$hint                 = New-Object System.Windows.Forms.Label
$hint.Text            = "Жми клавиши. Esc — выход."
$hint.ForeColor       = [System.Drawing.Color]::FromArgb(120, 130, 132)
$hint.Font            = New-Object System.Drawing.Font("Segoe UI", 9)
$hint.Location        = New-Object System.Drawing.Point(20, 14)
$hint.Size            = New-Object System.Drawing.Size(500, 20)
$form.Controls.Add($hint)

$big                  = New-Object System.Windows.Forms.Label
$big.Text             = "—"
$big.ForeColor        = [System.Drawing.Color]::FromArgb(4, 255, 243)
$big.Font             = New-Object System.Drawing.Font("Segoe UI", 30, [System.Drawing.FontStyle]::Bold)
$big.Location         = New-Object System.Drawing.Point(20, 40)
$big.Size             = New-Object System.Drawing.Size(500, 55)
$form.Controls.Add($big)

$detail               = New-Object System.Windows.Forms.Label
$detail.Text          = ""
$detail.ForeColor     = [System.Drawing.Color]::FromArgb(218, 218, 218)
$detail.Font          = New-Object System.Drawing.Font("Consolas", 11)
$detail.Location      = New-Object System.Drawing.Point(20, 100)
$detail.Size          = New-Object System.Drawing.Size(500, 60)
$form.Controls.Add($detail)

$log                  = New-Object System.Windows.Forms.TextBox
$log.Multiline        = $true
$log.ScrollBars       = "Vertical"
$log.ReadOnly         = $true
$log.BackColor        = [System.Drawing.Color]::FromArgb(12, 24, 25)
$log.ForeColor        = [System.Drawing.Color]::FromArgb(218, 218, 218)
$log.Font             = New-Object System.Drawing.Font("Consolas", 10)
$log.Location         = New-Object System.Drawing.Point(20, 170)
$log.Size             = New-Object System.Drawing.Size(500, 390)
$log.BorderStyle      = "FixedSingle"
$form.Controls.Add($log)

$script:seen = @{}

$form.Add_KeyDown({
    param($sender, $e)

    if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Escape) {
        $form.Close()
        return
    }

    $e.SuppressKeyPress = $true

    $vkCode = [int]$e.KeyCode
    $scan   = [Win32.Map]::MapVirtualKey([uint32]$vkCode, $MAPVK_VK_TO_VSC)
    $name   = $e.KeyCode.ToString()

    $mods = @()
    if ($e.Control) { $mods += "Ctrl" }
    if ($e.Alt)     { $mods += "Alt" }
    if ($e.Shift)   { $mods += "Shift" }
    $modText = if ($mods.Count) { ($mods -join "+") + " + " } else { "" }

    $big.Text    = "$modText$name"
    $detail.Text = "virtual key : 0x{0:X2}  ({0})`r`nscan code   : 0x{1:X2}  ({1})" -f $vkCode, $scan

    $line = "{0,-18} vk=0x{1:X2}  scan=0x{2:X2}" -f "$modText$name", $vkCode, $scan
    if (-not $script:seen.ContainsKey($line)) {
        $script:seen[$line] = $true
        $log.AppendText($line + "`r`n")
    }
})

[void]$form.ShowDialog()

Write-Host ""
Write-Host "Уникальных клавиш зафиксировано: $($script:seen.Count)" -ForegroundColor Cyan
Write-Host ""
$script:seen.Keys | Sort-Object | ForEach-Object { "  $_" }
Write-Host ""
