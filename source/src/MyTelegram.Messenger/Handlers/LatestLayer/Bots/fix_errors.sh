#!/bin/bash

# Fix all "is not" pattern matching errors - change to proper InputPeer handling
for file in SetBotCommandsHandler.cs GetBotCommandsHandler.cs ResetBotCommandsHandler.cs; do
  # These files use obj.Scope which is IBotCommandScope, not IInputPeer
  # The pattern matching is correct, just need to handle the peer extraction differently
  echo "Skipping $file - needs manual fix"
done

# Fix BsonNull.Value errors
sed -i 's/peerId ?? BsonNull\.Value/peerId.HasValue ? (BsonValue)peerId.Value : BsonNull.Value/g' *.cs

# Fix unassigned inputUser errors - these happen when we use "is not" without declaring variable
# Need to change pattern from "if (obj.Bot is not TInputUser inputUser)" to proper check
for file in AllowSendMessageHandler.cs CanSendMessageHandler.cs UpdateStarRefProgramHandler.cs \
            ToggleUserEmojiStatusPermissionHandler.cs AddPreviewMediaHandler.cs \
            DeletePreviewMediaHandler.cs EditPreviewMediaHandler.cs GetPreviewMediasHandler.cs \
            ReorderPreviewMediasHandler.cs CheckDownloadFileParamsHandler.cs InvokeWebViewCustomMethodHandler.cs; do
  echo "Processing $file"
done

