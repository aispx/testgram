#!/bin/bash
v=$(head -n 1 ./version.txt)
currentDate=`date +%-m%d`
version=$v.$currentDate

currentDir=$(pwd)
parentFolder=$(dirname "$currentDir")
outputRootFolder="$parentFolder/out/local/$version"
sourceRootFolder="$parentFolder/source/src"

authServerOutputFolder="$outputRootFolder/auth"
gatewayOutputFolder="$outputRootFolder/gateway"
messengerProCommandOutputFolder="$outputRootFolder/command-server"
messengerProQueryOutputFolder="$outputRootFolder/query-server"
messengerGrpcOutputFolder="$outputRootFolder/messenger-grpc"
smsOutputFolder="$outputRootFolder/sms"
dataSeederOutputFolder="$outputRootFolder/data-seeder"

echo "Current dir: $currentDir"
echo "OutputFolder is: $outputRootFolder"

CreateFolderIfNotExists() {
    folder="$1"
    if [ ! -d "$folder" ]; then
        echo "Create new folder: $folder"
        mkdir -p "$folder"
    fi
}

Build-Server() {
    srcFolder="$1"
    outputFolder="$2"
    sourceFolder="$sourceRootFolder/$srcFolder"
    dotnet publish "$sourceFolder" -c Release -o "$outputFolder"
}

Build-Target() {
    target="$1"
    case "$target" in
        auth|auth-server)
            Build-Server "./MyTelegram.AuthServer" "$authServerOutputFolder"
            ;;
        gateway|gateway-server)
            Build-Server "./MyTelegram.GatewayServer" "$gatewayOutputFolder"
            ;;
        command|command-server|messenger-command|message|messages)
            Build-Server "./MyTelegram.Messenger.CommandServer" "$messengerProCommandOutputFolder"
            ;;
        query|query-server|messenger-query)
            Build-Server "./MyTelegram.Messenger.QueryServer" "$messengerProQueryOutputFolder"
            ;;
        sms|sms-sender)
            Build-Server "./MyTelegram.SmsSender" "$smsOutputFolder"
            ;;
        data-seeder|seeder)
            Build-Server "./MyTelegram.DataSeeder" "$dataSeederOutputFolder"
            ;;
        all)
            Build-Target auth
            Build-Target gateway
            Build-Target command
            Build-Target query
            #Build-Server "./MyTelegram.MessengerServer.GrpcService" "$messengerGrpcOutputFolder"
            Build-Target sms
            Build-Target data-seeder
            ;;
        *)
            echo "Unknown target: $target"
            echo "Usage: ./build.sh [all|auth|gateway|command|query|sms|data-seeder]..."
            exit 1
            ;;
    esac
}

if [ "$#" -eq 0 ]; then
    Build-Target all
else
    for target in "$@"; do
        Build-Target "$target"
    done
fi

cd "$currentDir"
