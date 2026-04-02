---
name: schema-jppgr-am
description: Search Telegram TL schema, compare layers, decode hex payloads, and find constructor IDs. Use when working with MTProto protocol, TL types, Telegram API methods, constructor IDs, or when debugging serialization issues.
allowed-tools: Bash(curl *)
argument-hint: <command> [args]
---

# Telegram TL Schema API

Access the schema.jppgr.am API to work with Telegram's Type Language (TL) schemas. This API aggregates schemas from multiple sources and updates every 6 hours.

## Available Commands

### Search for constructors/methods
```bash
curl -s 'https://schema.jppgr.am/api/search?input=$ARGUMENTS&limit=20&format=json' | python3 -m json.tool
```

### Compare two layers (find what changed)
```bash
# Usage: schema-jppgr-am diff <from_layer> <to_layer>
# Example: schema-jppgr-am diff 222 223
curl -s 'https://schema.jppgr.am/api/diff?from_layer=$ARGUMENTS[0]&to_layer=$ARGUMENTS[1]' | python3 -m json.tool
```

### Get full layer schema
```bash
# Usage: schema-jppgr-am layer <layer_number>
# Example: schema-jppgr-am layer 222
curl -s 'https://schema.jppgr.am/api/layer?layer=$ARGUMENTS&format=json' | python3 -m json.tool
```

### Decode hex payload
```bash
# Usage: schema-jppgr-am hex2object <hex_string> <layer>
# Example: schema-jppgr-am hex2object 05162463... 222
curl -s 'https://schema.jppgr.am/api/hex2object?hex=$ARGUMENTS[0]&layer=$ARGUMENTS[1]' | python3 -m json.tool
```

### List available layers
```bash
curl -s 'https://schema.jppgr.am/api/layers' | python3 -m json.tool
```

### Parse TL definition
```bash
# Usage: schema-jppgr-am parse <layer>
# Returns structured type information with unwrapped types, flag metadata, and vector detection
curl -s 'https://schema.jppgr.am/api/parse?layer=$ARGUMENTS' | python3 -m json.tool
```

## Common Use Cases

### Find constructor by name
When you need to find a specific TL constructor or method:
```bash
curl -s 'https://schema.jppgr.am/api/search?input=inputStickerSetItem&format=json' | python3 -m json.tool
```

### Check what changed between layers
When updating API layer support:
```bash
curl -s 'https://schema.jppgr.am/api/diff?from_layer=222&to_layer=223' | python3 -m json.tool
```

### Verify constructor ID
When debugging "Unsupported constructorId" errors:
```bash
# Search for the constructor
curl -s 'https://schema.jppgr.am/api/search?input=inputStickerSetItem&format=json' | python3 -m json.tool

# The constructor ID is the hex value after # in the definition
# Example: inputStickerSetItem#32da9e9c = 0x32da9e9c
```

### Decode binary payload
When debugging MTProto serialization:
```bash
curl -s 'https://schema.jppgr.am/api/hex2object?hex=YOUR_HEX_HERE&layer=222' | python3 -m json.tool
```

## Response Format

All endpoints return JSON. Key fields:

- **search**: Returns `{ "query": "...", "results": [...], "total": N }`
- **diff**: Returns `{ "from_layer": N, "to_layer": M, "added": [...], "removed": [...], "changed": [...] }`
- **layer**: Returns `{ "layer": N, "lines": ["constructor#id params = Type;", ...] }`
- **hex2object**: Returns decoded TL object with `_` field indicating constructor name
- **layers**: Returns `{ "layers": [{ "layer": N, "lineCount": M, "preview": bool }] }`

## Tips

1. **Constructor IDs are CRC32 hashes**: The hex ID after `#` in TL definitions is a CRC32 of the normalized definition
2. **Layer matters for hex2object**: Different layers have different constructor IDs, so always specify the correct layer
3. **Use search for quick lookups**: Faster than parsing full layer schemas
4. **Check diff when updating**: See exactly what changed between API layers

## Examples

### Example 1: Find inputStickerSetItem constructor
```bash
curl -s 'https://schema.jppgr.am/api/search?input=inputStickerSetItem' | python3 -m json.tool
```

Output shows:
```
inputStickerSetItem#32da9e9c document:InputDocument emoji:string mask_coords:flags.0?MaskCoords keywords:flags.1?string = InputStickerSetItem;
```

Constructor ID: `0x32da9e9c`

### Example 2: Compare layers 222 and 223
```bash
curl -s 'https://schema.jppgr.am/api/diff?from_layer=222&to_layer=223' | python3 -m json.tool
```

Shows added, removed, and changed constructors.

### Example 3: Get current layer
```bash
curl -s 'https://schema.jppgr.am/api/layer?layer=latest&format=json' | python3 -m json.tool | head -50
```

## Integration with Testgram

When implementing Telegram API methods in Testgram:

1. **Search for the method**: `schema-jppgr-am search messages.getStickerSet`
2. **Check official docs**: https://core.telegram.org/method/messages.getStickerSet
3. **Verify constructor IDs match**: Compare with official clients
4. **Implement handler**: Use the TL definition to build request/response types

## Troubleshooting

- **"No results found"**: Check spelling, try partial match
- **"Layer not found"**: Use `/api/layers` to see available layers
- **"Invalid hex"**: Ensure hex string is valid and complete
- **Constructor ID mismatch**: Different layers may have different IDs for the same type

## API Base URL

All requests go to: `https://schema.jppgr.am/api/`

No authentication required. Rate limits apply (reasonable use).
