#!/usr/bin/env python3
"""
Import wallpapers and themes from JSON files to MongoDB
"""

import json
import subprocess
import sys
import os

def run_mongosh(script):
    """Run mongosh command"""
    cmd = [
        'docker-compose', 'exec', '-T', 'mongodb',
        'mongosh', 'tg', '--quiet', '--eval', script
    ]
    
    result = subprocess.run(
        cmd,
        cwd='/root/testgram/docker/compose',
        capture_output=True,
        text=True
    )
    
    if result.returncode != 0:
        print(f"❌ Error: {result.stderr}")
        return False
    
    print(result.stdout.strip())
    return True

def import_wallpapers():
    """Import wallpapers from JSON"""
    if not os.path.exists('/tmp/wallpapers.json'):
        print("⚠️  /tmp/wallpapers.json not found, skipping wallpapers")
        return True
    
    print("\n📦 Importing wallpapers...")
    
    with open('/tmp/wallpapers.json', 'r') as f:
        wallpapers = json.load(f)
    
    script = f"""
const wallpapers = {json.dumps(wallpapers)};
db.wallpapers.deleteMany({{}});

for (const wp of wallpapers) {{
    const doc = {{
        _id: 'wallpaper-' + wp.id,
        WallpaperId: NumberLong(wp.id),
        AccessHash: NumberLong(wp.access_hash || 0),
        Slug: wp.slug || '',
        IsDefault: wp.default || false,
        IsPattern: wp.pattern || false,
        IsDark: wp.dark || false,
        DocumentId: NumberLong(wp.document_id || 0)
    }};
    
    if (wp.settings) {{
        doc.Settings = {{}};
        if (wp.settings.blur !== undefined) doc.Settings.Blur = wp.settings.blur;
        if (wp.settings.motion !== undefined) doc.Settings.Motion = wp.settings.motion;
        if (wp.settings.background_color !== undefined) doc.Settings.BackgroundColor = wp.settings.background_color;
        if (wp.settings.second_background_color !== undefined) doc.Settings.SecondBackgroundColor = wp.settings.second_background_color;
        if (wp.settings.third_background_color !== undefined) doc.Settings.ThirdBackgroundColor = wp.settings.third_background_color;
        if (wp.settings.fourth_background_color !== undefined) doc.Settings.FourthBackgroundColor = wp.settings.fourth_background_color;
        if (wp.settings.intensity !== undefined) doc.Settings.Intensity = wp.settings.intensity;
        if (wp.settings.rotation !== undefined) doc.Settings.Rotation = wp.settings.rotation;
    }}
    
    db.wallpapers.insertOne(doc);
}}

print('✅ Imported ' + db.wallpapers.countDocuments() + ' wallpapers');
"""
    
    return run_mongosh(script)

def import_themes():
    """Import chat themes from JSON"""
    if not os.path.exists('/tmp/chat_themes.json'):
        print("⚠️  /tmp/chat_themes.json not found, skipping themes")
        return True
    
    print("\n📦 Importing chat themes...")
    
    with open('/tmp/chat_themes.json', 'r') as f:
        themes = json.load(f)
    
    script = f"""
const themes = {json.dumps(themes)};
db.chat_themes.deleteMany({{}});

for (const theme of themes) {{
    const doc = {{
        _id: 'chat-theme-' + theme.emoticon,
        Emoticon: theme.emoticon,
        Type: 'emoji',
        LightTheme: theme.light || {{}},
        DarkTheme: theme.dark || {{}}
    }};
    
    db.chat_themes.insertOne(doc);
}}

print('✅ Imported ' + db.chat_themes.countDocuments() + ' chat themes');
"""
    
    return run_mongosh(script)

def main():
    """Main function"""
    print("📦 Importing to MongoDB...")
    
    wp_ok = import_wallpapers()
    th_ok = import_themes()
    
    if wp_ok and th_ok:
        print("\n✅ Import complete!")
        return 0
    else:
        print("\n❌ Import failed")
        return 1

if __name__ == '__main__':
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n\n⚠️  Interrupted by user")
        sys.exit(1)
    except Exception as e:
        print(f"\n❌ Fatal error: {e}")
        sys.exit(1)
