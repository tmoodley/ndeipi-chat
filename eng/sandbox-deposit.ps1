<#
.SYNOPSIS
    Adds test money to someone's Bridge **sandbox** wallet, by their email: Bridge's simulate_deposit,
    which only exists in the sandbox. Nothing real moves.

.DESCRIPTION
    Finds the Bridge customer with that email, then their wallet, and simulates a deposit into it.
    The sandbox API key comes from $env:BRIDGE_SANDBOX_KEY (never put it in this file or the repo).

.EXAMPLE
    $env:BRIDGE_SANDBOX_KEY = 'sk-test-...'
    ./eng/sandbox-deposit.ps1 -Email someone@example.com -Amount 50
#>
param(
    [Parameter(Mandatory)] [string] $Email,
    [decimal] $Amount = 100,
    # What Ndeipi sends between wallets (Bridge:Currency).
    [string] $Currency = 'usdc'
)

$ErrorActionPreference = 'Stop'
$key = $env:BRIDGE_SANDBOX_KEY
if (-not $key) { throw 'Set $env:BRIDGE_SANDBOX_KEY to the Bridge sandbox API key first.' }
if (-not $key.StartsWith('sk-test-')) { throw 'That isn''t a sandbox key (sk-test-...). This only works in the sandbox.' }
if ($Amount -le 0) { throw 'The amount has to be more than zero.' }

$api = 'https://api.sandbox.bridge.xyz/v0'
$headers = @{ 'Api-Key' = $key }

# Find the customer by email (paging through, 100 at a time).
$customer = $null
$cursor = $null
do {
    $uri = "$api/customers?limit=100" + ($(if ($cursor) { "&starting_after=$cursor" } else { '' }))
    $page = Invoke-RestMethod -Uri $uri -Headers $headers
    $list = @(if ($page.data) { $page.data } else { $page })
    $customer = $list | Where-Object { $_.email -eq $Email } | Select-Object -First 1
    $cursor = if ($list.Count -eq 100) { $list[-1].id } else { $null }
} while (-not $customer -and $cursor)
if (-not $customer) { throw "No Bridge sandbox customer has the email $Email. They need to verify under Wallet in Ndeipi first." }

$wallets = Invoke-RestMethod -Uri "$api/customers/$($customer.id)/wallets" -Headers $headers
$wallet = @(if ($wallets.data) { $wallets.data } else { $wallets }) | Select-Object -First 1
if (-not $wallet) { throw "$Email has no wallet yet: it's created once they're verified." }

$body = @{ amount = $Amount.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture); currency = $Currency } | ConvertTo-Json
$result = Invoke-RestMethod -Method Post -Uri "$api/customers/$($customer.id)/wallets/$($wallet.id)/simulate_deposit" `
    -Headers ($headers + @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }) -ContentType 'application/json' -Body $body

$balance = $result.balances | Where-Object { $_.currency -eq $Currency } | Select-Object -First 1
Write-Host "Added $Amount $($Currency.ToUpperInvariant()) to $Email's sandbox wallet ($($wallet.address)). Balance now: $($balance.balance) $($Currency.ToUpperInvariant())."
