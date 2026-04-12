import re

files = ['SetBotCommandsHandler.cs', 'GetBotCommandsHandler.cs', 'ResetBotCommandsHandler.cs']

for fname in files:
    with open(fname, 'r') as f:
        content = f.read()
    
    # Fix TInputPeerUser/Chat/Channel pattern matching
    content = re.sub(
        r'TInputPeerUser peerUser => peerUser\.UserId',
        r'TInputPeerUser peerUser => peerUser.UserId',
        content
    )
    content = re.sub(
        r'TInputPeerChat peerChat => peerChat\.ChatId',
        r'TInputPeerChat peerChat => peerChat.ChatId',
        content
    )
    content = re.sub(
        r'TInputPeerChannel peerChannel => peerChannel\.ChannelId',
        r'TInputPeerChannel peerChannel => peerChannel.ChannelId',
        content
    )
    
    # Fix BsonNull.Value usage
    content = re.sub(
        r'peerId \?\? BsonNull\.Value',
        r'peerId.HasValue ? (BsonValue)peerId.Value : BsonNull.Value',
        content
    )
    
    with open(fname, 'w') as f:
        f.write(content)
    
    print(f"Fixed {fname}")

