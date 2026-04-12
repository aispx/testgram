import re

# Fix AddPreviewMediaHandler - remove LangCode field
with open('AddPreviewMediaHandler.cs', 'r') as f:
    content = f.read()
content = re.sub(
    r'return new TBotPreviewMedia\s*\{\s*LangCode = obj\.LangCode \?\? "",\s*Date = \(int\)DateTimeOffset\.UtcNow\.ToUnixTimeSeconds\(\)\s*\};',
    'return new TBotPreviewMedia\n        {\n            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),\n            Media = new TMessageMediaEmpty()\n        };',
    content, flags=re.DOTALL
)
with open('AddPreviewMediaHandler.cs', 'w') as f:
    f.write(content)
print("Fixed AddPreviewMediaHandler.cs")

# Fix EditPreviewMediaHandler - remove LangCode field
with open('EditPreviewMediaHandler.cs', 'r') as f:
    content = f.read()
content = re.sub(
    r'return new TBotPreviewMedia\s*\{\s*LangCode = obj\.LangCode \?\? "",\s*Date = \(int\)DateTimeOffset\.UtcNow\.ToUnixTimeSeconds\(\)\s*\};',
    'return new TBotPreviewMedia\n        {\n            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),\n            Media = new TMessageMediaEmpty()\n        };',
    content, flags=re.DOTALL
)
with open('EditPreviewMediaHandler.cs', 'w') as f:
    f.write(content)
print("Fixed EditPreviewMediaHandler.cs")

# Fix GetPreviewMediasHandler - remove LangCode field
with open('GetPreviewMediasHandler.cs', 'r') as f:
    content = f.read()
content = re.sub(
    r'result\.Add\(new TBotPreviewMedia\s*\{\s*LangCode = doc\["LangCode"\]\.AsString,\s*Date = [^\}]+\}\);',
    'result.Add(new TBotPreviewMedia\n            {\n                Date = doc.Contains("CreatedAt") ? (int)doc["CreatedAt"].ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds : 0,\n                Media = new TMessageMediaEmpty()\n            });',
    content, flags=re.DOTALL
)
with open('GetPreviewMediasHandler.cs', 'w') as f:
    f.write(content)
print("Fixed GetPreviewMediasHandler.cs")

