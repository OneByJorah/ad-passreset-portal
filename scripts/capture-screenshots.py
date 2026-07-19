#!/usr/bin/env python3
"""Capture ad-passreset-portal screenshots using Playwright HTML mockups.

Usage:
    python scripts/capture-screenshots.py

Prerequisites:
    pip install playwright
    python -m playwright install chromium

This script captures screenshots of the login, reset, and success views
and saves them to docs/screenshots/ for use in the README.
"""
from playwright.sync_api import sync_playwright
import time
import os

SCREENSHOT_DIR = os.environ.get("SCREENSHOT_DIR", "docs/screenshots")

# HTML mockups matching the actual app design
LOGIN_HTML = r"""
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Change Password</title>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" rel="stylesheet">
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', sans-serif; background: #0f172a; min-height: 100vh; display: flex; align-items: center; justify-content: center; }
        .card { background: #1e293b; border-radius: 16px; padding: 40px; width: 420px; box-shadow: 0 25px 50px -12px rgba(0,0,0,0.5); }
        .logo { text-align: center; margin-bottom: 32px; }
        .logo h1 { color: #f8fafc; font-size: 24px; font-weight: 700; }
        .logo p { color: #94a3b8; font-size: 14px; margin-top: 8px; }
        .form-group { margin-bottom: 20px; }
        .form-group label { display: block; color: #cbd5e1; font-size: 14px; font-weight: 500; margin-bottom: 8px; }
        .form-group input { width: 100%; padding: 12px 16px; background: #0f172a; border: 1px solid #334155; border-radius: 8px; color: #f8fafc; font-size: 14px; }
        .form-group input:focus { outline: none; border-color: #3b82f6; }
        .btn { width: 100%; padding: 14px; background: #3b82f6; color: white; border: none; border-radius: 8px; font-size: 16px; font-weight: 600; cursor: pointer; }
        .status { display: flex; align-items: center; gap: 8px; padding: 12px; background: #064e3b; border-radius: 8px; margin-top: 24px; }
        .status-dot { width: 8px; height: 8px; background: #10b981; border-radius: 50%; }
        .status-text { color: #6ee7b7; font-size: 12px; }
    </style>
</head>
<body>
    <div class="card">
        <div class="logo"><h1>🔐 AD Passreset Portal</h1><p>Self-service Active Directory password change</p></div>
        <form>
            <div class="form-group"><label>Domain\Username</label><input type="text" placeholder="DOMAIN\username"></div>
            <div class="form-group"><label>Current Password</label><input type="password" placeholder="Enter current password"></div>
            <div class="form-group"><label>New Password</label><input type="password" placeholder="Enter new password"></div>
            <div class="form-group"><label>Confirm Password</label><input type="password" placeholder="Confirm new password"></div>
            <button type="button" class="btn">Change Password</button>
        </form>
        <div class="status"><div class="status-dot"></div><div class="status-text">LDAP Connected</div></div>
    </div>
</body></html>
"""

RESET_HTML = r"""
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Change Password</title>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" rel="stylesheet">
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', sans-serif; background: #0f172a; min-height: 100vh; display: flex; align-items: center; justify-content: center; }
        .card { background: #1e293b; border-radius: 16px; padding: 40px; width: 420px; box-shadow: 0 25px 50px -12px rgba(0,0,0,0.5); }
        .logo { text-align: center; margin-bottom: 32px; }
        .logo h1 { color: #f8fafc; font-size: 24px; font-weight: 700; }
        .logo p { color: #94a3b8; font-size: 14px; margin-top: 8px; }
        .form-group { margin-bottom: 20px; }
        .form-group label { display: block; color: #cbd5e1; font-size: 14px; font-weight: 500; margin-bottom: 8px; }
        .form-group input { width: 100%; padding: 12px 16px; background: #0f172a; border: 1px solid #334155; border-radius: 8px; color: #f8fafc; font-size: 14px; }
        .btn { width: 100%; padding: 14px; background: #3b82f6; color: white; border: none; border-radius: 8px; font-size: 16px; font-weight: 600; cursor: pointer; }
        .strength { height: 4px; background: #334155; border-radius: 2px; margin-top: 8px; }
        .strength-fill { height: 100%; width: 75%; background: #10b981; border-radius: 2px; }
    </style>
</head>
<body>
    <div class="card">
        <div class="logo"><h1>🔐 AD Passreset Portal</h1><p>Self-service Active Directory password change</p></div>
        <form>
            <div class="form-group"><label>Domain\Username</label><input type="text" value="COMPANY\\john.doe"></div>
            <div class="form-group"><label>Current Password</label><input type="password" value="••••••••••"></div>
            <div class="form-group"><label>New Password</label><input type="password" value="N3wP@ssw0rd!"><div class="strength"><div class="strength-fill"></div></div></div>
            <div class="form-group"><label>Confirm Password</label><input type="password" value="N3wP@ssw0rd!"></div>
            <button type="button" class="btn">Change Password</button>
        </form>
    </div>
</body></html>
"""

SUCCESS_HTML = r"""
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Password Changed</title>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" rel="stylesheet">
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', sans-serif; background: #0f172a; min-height: 100vh; display: flex; align-items: center; justify-content: center; }
        .card { background: #1e293b; border-radius: 16px; padding: 40px; width: 420px; text-align: center; box-shadow: 0 25px 50px -12px rgba(0,0,0,0.5); }
        .icon { width: 80px; height: 80px; background: #065f46; border-radius: 50%; display: flex; align-items: center; justify-content: center; margin: 0 auto 24px; }
        .icon svg { width: 40px; height: 40px; stroke: #10b981; }
        h1 { color: #f8fafc; font-size: 24px; font-weight: 700; margin-bottom: 12px; }
        p { color: #94a3b8; font-size: 14px; line-height: 1.6; margin-bottom: 24px; }
        .btn { display: inline-block; padding: 12px 24px; background: #3b82f6; color: white; border: none; border-radius: 8px; font-size: 14px; font-weight: 600; cursor: pointer; }
        .timer { color: #64748b; font-size: 12px; margin-top: 16px; }
    </style>
</head>
<body>
    <div class="card">
        <div class="icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M20 6L9 17l-5-5"></path></svg></div>
        <h1>Password Changed Successfully</h1>
        <p>Your Active Directory password has been updated. Please use your new password for all subsequent logins.</p>
        <button type="button" class="btn">Return to Login</button>
        <p class="timer">This page will redirect in 10 seconds</p>
    </div>
</body></html>
"""

os.makedirs(SCREENSHOT_DIR, exist_ok=True)

def capture_screenshots():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page(viewport={"width": 1920, "height": 1080})
        
        pages = [
            ("login.png", LOGIN_HTML),
            ("reset.png", RESET_HTML),
            ("success.png", SUCCESS_HTML),
        ]
        
        for name, html in pages:
            print(f"Capturing {name}...")
            page.set_content(html)
            page.wait_for_load_state("networkidle")
            time.sleep(1)
            path = os.path.join(SCREENSHOT_DIR, name)
            page.screenshot(path=path, full_page=False)
            print(f"Saved: {path} ({os.path.getsize(path):,} bytes)")
        
        browser.close()
    print("\nAll screenshots captured successfully!")

if __name__ == "__main__":
    capture_screenshots()
