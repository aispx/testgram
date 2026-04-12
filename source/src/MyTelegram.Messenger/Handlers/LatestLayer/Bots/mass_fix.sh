#!/bin/bash

# Fix "is not" pattern - change to proper null check with variable assignment
for file in AddPreviewMediaHandler.cs AllowSendMessageHandler.cs CanSendMessageHandler.cs \
            CheckDownloadFileParamsHandler.cs DeletePreviewMediaHandler.cs EditPreviewMediaHandler.cs \
            GetPreviewMediasHandler.cs InvokeWebViewCustomMethodHandler.cs ReorderPreviewMediasHandler.cs \
            ToggleUserEmojiStatusPermissionHandler.cs UpdateStarRefProgramHandler.cs; do
  
  sed -i 's/if (obj\.Bot is not TInputUser inputUser)/if (obj.Bot is not TInputUser)\n            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();\n\n        var inputUser = (TInputUser)obj.Bot;/g' "$file"
done

# Fix UpdateUserEmojiStatusHandler - different field name
sed -i 's/if (obj\.User is not TInputUser inputUser)/if (obj.UserId is not TInputUser)\n            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();\n\n        var inputUser = (TInputUser)obj.UserId;/g' UpdateUserEmojiStatusHandler.cs

