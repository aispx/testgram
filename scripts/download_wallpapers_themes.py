#!/usr/bin/env python3
"""
Download wallpapers and themes from official Telegram
Requires: TG_API_ID, TG_API_HASH, TG_PHONE environment variables
"""

import asyncio
import json
import os
import sys
from telethon import TelegramClient
from telethon.tl.functions.account import GetWallPapersRequest, GetChatThemesRequest
from telethon.tl.types import WallPaper, WallPaperNoFile

API_ID = int(os.getenv('TG_API_ID', '0'))
API_HASH = os.getenv('TG_API_HASH', '')
PHONE = os.getenv('TG_PHONE', '')

if API_ID == 0 or not API_HASH:
    print("❌ Error: Set TG_API_ID and TG_API_HASH environment variables")
    print("Get them from https://my.telegram.org/apps")
    print("")
    print("Usage:")
    print("  export TG_API_ID=12345678")
    print("  export TG_API_HASH=abcdef1234567890abcdef1234567890")
    print("  export TG_PHONE=+1234567890")
    print("  python3 scripts/download_wallpapers_themes.py")
    sys.exit(1)

async def download_wallpapers(client):
    """Download wallpapers from Telegram"""
    print("\n📥 Downloading wallpapers...")
    try:
        result = await client(GetWallPapersRequest(hash=0))
        wallpapers = []
        
        for wp in result.wallpapers:
            data = {'id': wp.id}
            
            if isinstance(wp, WallPaper):
                data.update({
                    'access_hash': wp.access_hash,
                    'slug': wp.slug,
                    'default': getattr(wp, 'default', False),
                    'pattern': getattr(wp, 'pattern', False),
                    'dark': getattr(wp, 'dark', False),
                    'document_id': wp.document.id if wp.document else 0
                })
                
                if wp.settings:
                    settings = {}
                    for attr in ['blur', 'motion', 'background_color', 'second_background_color', 
                                'third_background_color', 'fourth_background_color', 'intensity', 'rotation']:
                        if hasattr(wp.settings, attr):
                            val = getattr(wp.settings, attr)
                            if val is not None:
                                settings[attr] = val
                    if settings:
                        data['settings'] = settings
                
                print(f"  ✓ {wp.slug}")
            
            elif isinstance(wp, WallPaperNoFile):
                data.update({
                    'type': 'no_file',
                    'default': getattr(wp, 'default', False),
                    'dark': getattr(wp, 'dark', False)
                })
                
                if wp.settings:
                    settings = {}
                    for attr in ['background_color', 'second_background_color', 
                                'third_background_color', 'fourth_background_color', 'rotation']:
                        if hasattr(wp.settings, attr):
                            val = getattr(wp.settings, attr)
                            if val is not None:
                                settings[attr] = val
                    if settings:
                        data['settings'] = settings
                
                print(f"  ✓ Gradient/Solid (ID: {wp.id})")
            
            wallpapers.append(data)
        
        with open('/tmp/wallpapers.json', 'w') as f:
            json.dump(wallpapers, f, indent=2)
        
        print(f"\n✅ Saved {len(wallpapers)} wallpapers to /tmp/wallpapers.json")
        return True
    
    except Exception as e:
        print(f"❌ Error downloading wallpapers: {e}")
        return False

async def download_themes(client):
    """Download chat themes from Telegram"""
    print("\n📥 Downloading chat themes...")
    try:
        result = await client(GetChatThemesRequest(hash=0))
        themes = []
        
        for theme in result.themes:
            data = {'emoticon': theme.emoticon}
            
            if theme.theme:
                data['light'] = {
                    'id': getattr(theme.theme, 'id', 0),
                    'slug': getattr(theme.theme, 'slug', ''),
                    'title': getattr(theme.theme, 'title', '')
                }
            
            if theme.dark_theme:
                data['dark'] = {
                    'id': getattr(theme.dark_theme, 'id', 0),
                    'slug': getattr(theme.dark_theme, 'slug', ''),
                    'title': getattr(theme.dark_theme, 'title', '')
                }
            
            themes.append(data)
            print(f"  ✓ {theme.emoticon}")
        
        with open('/tmp/chat_themes.json', 'w') as f:
            json.dump(themes, f, indent=2)
        
        print(f"\n✅ Saved {len(themes)} chat themes to /tmp/chat_themes.json")
        return True
    
    except Exception as e:
        print(f"❌ Error downloading themes: {e}")
        return False

async def main():
    """Main function"""
    print("🔐 Connecting to Telegram...")
    
    client = TelegramClient('tg_session', API_ID, API_HASH)
    await client.start(phone=PHONE)
    
    print("✅ Connected!")
    
    # Download wallpapers and themes
    wp_ok = await download_wallpapers(client)
    th_ok = await download_themes(client)
    
    await client.disconnect()
    
    if wp_ok and th_ok:
        print("\n✅ Download complete!")
        print("Files saved:")
        print("  - /tmp/wallpapers.json")
        print("  - /tmp/chat_themes.json")
        print("\nRun ./scripts/import-to-mongodb.sh to import to database")
        return 0
    else:
        print("\n⚠️  Download completed with errors")
        return 1

if __name__ == '__main__':
    try:
        exit_code = asyncio.run(main())
        sys.exit(exit_code)
    except KeyboardInterrupt:
        print("\n\n⚠️  Interrupted by user")
        sys.exit(1)
    except Exception as e:
        print(f"\n❌ Fatal error: {e}")
        sys.exit(1)
