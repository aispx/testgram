$version=Get-Content "./version.txt"
$date = Get-Date -Format "Mdd"
$version = $version+$date

$currentDir=(Get-Item .).FullName
$parentFolder=(Get-Item $currentDir).Parent
$outputRootFolder=Join-Path $parentFolder "out/local" $version 
$sourceRootFolder=Join-Path $parentFolder "./source/src"

$authServerOutputFolder=Join-Path $outputRootFolder "auth"
$gatewayOutputFolder=Join-Path $outputRootFolder "gateway"
$messengerProCommandOutputFolder=Join-Path $outputRootFolder "command-server"
$messengerProQueryOutputFolder=Join-Path $outputRootFolder "query-server"
$messengerGrpcOutputFolder=Join-Path $outputRootFolder "messenger-grpc"
$smsOutputFolder=Join-Path $outputRootFolder "sms"
$dataSeederOutputFolder=Join-Path $outputRootFolder "data-seeder"

Write-Output "Current dir:$currentDir"
Write-Output "OutputFolder is:$outputRootFolder"

function CreateFolderIfNotExists([System.String] $folder){
    if(![System.IO.Directory]::Exists($folder)){
        Write-Output "Create new folder:$folder"
        [System.IO.Directory]::CreateDirectory($folder)
    }
}

function Build-Server([System.String]$srcFolder,[System.String] $outputFolder) {
    $sourceFolder=Join-Path $sourceRootFolder $srcFolder
    dotnet publish $sourceFolder -c Release -o $outputFolder
}

function Build-Target([System.String]$target) {
    switch ($target) {
        { $_ -in @("auth","auth-server") } {
            Build-Server "./MyTelegram.AuthServer" $authServerOutputFolder
            break
        }
        { $_ -in @("gateway","gateway-server") } {
            Build-Server "./MyTelegram.GatewayServer" $gatewayOutputFolder
            break
        }
        { $_ -in @("command","command-server","messenger-command","message","messages") } {
            Build-Server "./MyTelegram.Messenger.CommandServer" $messengerProCommandOutputFolder
            break
        }
        { $_ -in @("query","query-server","messenger-query") } {
            Build-Server "./MyTelegram.Messenger.QueryServer" $messengerProQueryOutputFolder
            break
        }
        { $_ -in @("sms","sms-sender") } {
            Build-Server "./MyTelegram.SmsSender" $smsOutputFolder
            break
        }
        { $_ -in @("data-seeder","seeder") } {
            Build-Server "./MyTelegram.DataSeeder" $dataSeederOutputFolder
            break
        }
        "all" {
            Build-Target "auth"
            Build-Target "gateway"
            Build-Target "command"
            Build-Target "query"
            #Build-Server "./MyTelegram.MessengerServer.GrpcService" $messengerGrpcOutputFolder
            Build-Target "sms"
            Build-Target "data-seeder"
            break
        }
        default {
            Write-Error "Unknown target: $target"
            Write-Output "Usage: ./build.ps1 [all|auth|gateway|command|query|sms|data-seeder]..."
            exit 1
        }
    }
}

# Set-Location ../source/
#dotnet restore ./MyTelegram.sln
if ($args.Count -eq 0) {
    Build-Target "all"
} else {
    foreach ($target in $args) {
        Build-Target $target
    }
}

Set-Location $currentDir


