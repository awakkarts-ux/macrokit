# inject-test.ps1
# Проверяет, что программно отправленные нажатия доходят до активного окна,
# и с какой реальной точностью выдерживаются паузы.
# Это проверка механизма ВЫВОДА — основы будущего макро-движка.
#
# Как пользоваться:
#   1. Запусти скрипт
#   2. За время обратного отсчёта переключись в любое окно (блокнот, редактор, что угодно)
#   3. Смотри, что туда напечаталось
#
# Примеры:
#   .\inject-test.ps1                          — отправит "1" через 5 секунд
#   .\inject-test.ps1 -Keys 1,2,3 -DelayMs 150 — отправит последовательность
#   .\inject-test.ps1 -Keys 1 -UseVirtualKey   — то же, но другим способом (для сравнения)

param(
    [string[]]$Keys = @('1'),
    [int]$DelayMs = 120,
    [int]$HoldMs = 30,
    [int]$CountdownSec = 5,
    [switch]$UseVirtualKey
)

Add-Type -Namespace Win32 -Name Kbd -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

[DllImport("user32.dll")]
public static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
'@

$KEYEVENTF_KEYUP     = 0x0002
$KEYEVENTF_SCANCODE  = 0x0008

# Scan-коды (set 1) — именно их читают игры на DirectInput/CryEngine
$scan = @{
    '1'='0x02'; '2'='0x03'; '3'='0x04'; '4'='0x05'; '5'='0x06'
    '6'='0x07'; '7'='0x08'; '8'='0x09'; '9'='0x0A'; '0'='0x0B'
    'q'='0x10'; 'w'='0x11'; 'e'='0x12'; 'r'='0x13'; 't'='0x14'
    'a'='0x1E'; 's'='0x1F'; 'd'='0x20'; 'f'='0x21'; 'g'='0x22'
    'z'='0x2C'; 'x'='0x2D'; 'c'='0x2E'; 'v'='0x2F'
    'f1'='0x3B'; 'f2'='0x3C'; 'f3'='0x3D'; 'f4'='0x3E'; 'f5'='0x3F'; 'f6'='0x40'
    'f7'='0x41'; 'f8'='0x42'; 'f9'='0x43'; 'f10'='0x44'; 'f11'='0x57'; 'f12'='0x58'
}

# Виртуальные коды — альтернативный способ, для сравнения
$vk = @{
    '1'='0x31'; '2'='0x32'; '3'='0x33'; '4'='0x34'; '5'='0x35'
    '6'='0x36'; '7'='0x37'; '8'='0x38'; '9'='0x39'; '0'='0x30'
    'q'='0x51'; 'w'='0x57'; 'e'='0x45'; 'r'='0x52'; 't'='0x54'
    'a'='0x41'; 's'='0x53'; 'd'='0x44'; 'f'='0x46'; 'g'='0x47'
    'z'='0x5A'; 'x'='0x58'; 'c'='0x43'; 'v'='0x56'
    'f1'='0x70'; 'f2'='0x71'; 'f3'='0x72'; 'f4'='0x73'; 'f5'='0x74'; 'f6'='0x75'
    'f7'='0x76'; 'f8'='0x77'; 'f9'='0x78'; 'f10'='0x79'; 'f11'='0x7A'; 'f12'='0x7B'
}

$table = if ($UseVirtualKey) { $vk } else { $scan }
$mode  = if ($UseVirtualKey) { 'virtual key' } else { 'scan code' }

# Проверяем, что все клавиши известны
foreach ($k in $Keys) {
    if (-not $table.ContainsKey($k.ToLower())) {
        Write-Host "Неизвестная клавиша: '$k'. Доступны: $($table.Keys -join ', ')" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "  Режим отправки : $mode" -ForegroundColor Cyan
Write-Host "  Последовательность: $($Keys -join ' -> ')" -ForegroundColor Cyan
Write-Host "  Пауза между клавишами: $DelayMs мс, удержание: $HoldMs мс" -ForegroundColor Cyan
Write-Host ""
Write-Host "  ПЕРЕКЛЮЧИСЬ В НУЖНОЕ ОКНО" -ForegroundColor Yellow
Write-Host ""

for ($i = $CountdownSec; $i -gt 0; $i--) {
    Write-Host "`r  Отправка через $i... " -NoNewline -ForegroundColor Yellow
    Start-Sleep -Milliseconds 1000
}
Write-Host "`r  Отправляю...            " -ForegroundColor Green

# Куда именно ушёл ввод
$sb = New-Object System.Text.StringBuilder 512
[void][Win32.Kbd]::GetWindowTextW([Win32.Kbd]::GetForegroundWindow(), $sb, 512)
$target = $sb.ToString()

$sw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($k in $Keys) {
    $code = [byte][int]$table[$k.ToLower()]

    if ($UseVirtualKey) {
        [Win32.Kbd]::keybd_event($code, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds $HoldMs
        [Win32.Kbd]::keybd_event($code, 0, $KEYEVENTF_KEYUP, [UIntPtr]::Zero)
    } else {
        [Win32.Kbd]::keybd_event(0, $code, $KEYEVENTF_SCANCODE, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds $HoldMs
        [Win32.Kbd]::keybd_event(0, $code, ($KEYEVENTF_SCANCODE -bor $KEYEVENTF_KEYUP), [UIntPtr]::Zero)
    }

    Start-Sleep -Milliseconds $DelayMs
}

$sw.Stop()

Write-Host ""
Write-Host "  Готово." -ForegroundColor Green
Write-Host "  Ввод ушёл в окно : $target"
Write-Host "  Заняло           : $($sw.ElapsedMilliseconds) мс (ожидалось ~$(($DelayMs + $HoldMs) * $Keys.Count) мс)"
Write-Host ""
Write-Host "  Если клавиши напечатались — способ рабочий, на нём строим движок." -ForegroundColor Gray
Write-Host "  Если нет — попробуй: .\inject-test.ps1 -Keys $($Keys -join ',') -UseVirtualKey" -ForegroundColor Gray
Write-Host ""
