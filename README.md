# OrangeCrypt 🍊🔒 (A DeepSeek-Powered Concept Demo)

A Windows Folder Encryption Tool.

**⚠️ Warning: This is a hobby project glued together with AI help. It’s fragile, insecure, and absolutely NOT for real use. You’ve been warned!**

> **How this project happened:**  
> `1. Me: "Hey DeepSeek, I have this wild idea..."` →  
> `2. DeepSeek: "Ooh, neat! Try using Dokany + DiscUtils?"` →  
> `3. Me: "...can you write the code?"` →  
> `4. *Copy. Paste. Tweak. Pray.*` →  
> `5. IT RUNS! 🎉 (kinda)`  
> **So yeah – this exists thanks to [DeepSeek-R1](https://deepseek.com). No AI, no OrangeCrypt. Simple as that.**

This is **NOT** a serious, secure, or production-ready encryption tool. It's a **proof-of-concept** messing around with some cool ideas (maybe). **Use at your own risk!** Seriously, **do NOT put important files in here.** Things *can* and *probably will* go wrong eventually.

## What is this... thing?

It's a C# program for Windows that *tries* to make a folder look like a single encrypted file (ending in `.ofcrypt`). When you "open" that file with the correct password, it *attempts* to magically mount the decrypted folder back in its original location (as a hidden folder).

## Brief Workflow

1.  **Setup:** You run `OrangeCrypt.exe /install` once (needs Admin). This sets up file associations and some background stuff.
2.  **"Encrypting":** You tell OrangeCrypt to lock a folder (e.g., `C:\MySecretStuff`). It:
    *   Creates a big (up to 100GB!) encrypted container file next to it (`C:\MySecretStuff.ofcrypt`).
    *   **Copies** everything from `MySecretStuff` *into* this container.
    *   Deletes the original `MySecretStuff` folder. It **doesn't** securely delete the originals! (Big flaw #1).
3.  **"Opening" (Mounting):** Double-click the `.ofcrypt` file. Enter password.
    *   If correct, it uses [Dokany](https://github.com/dokan-dev/dokany/releases/) (which you **MUST install first!**) to mount the container.
    *   It *pretends* the decrypted folder is back at `C:\MySecretStuff` (but it's actually a mount point to the container).
4.  **Working:** Use the folder like normal (hopefully). Files read/written get encrypted/decrypted on the fly.
5.  **"Closing" (Unmounting):** Right-click the tray icon -> "Unmount All". Next time you double click the container, it'll ask you for password again.

## How it *barely* works (Technical Gist - Don't Expect Much)

Under the hood, it does this:

1.  **The `.ofcrypt` file** is basically a wrapper around a **sparse VHD file** (handled by DiscUtils).
2.  **We need to trick DiscUtils** into thinking it's reading/writing a normal, unencrypted VHD stream. But obviously, the whole file *should* be encrypted.
3.  **So, we made (well, DeepSeek made) two special streams:**
    *   **Header Offset Stream:** This lets us carve out a little space *at the start* of the `.ofcrypt` file to store our own stuff (like the encrypted encryption key, some metadata). DiscUtils doesn't see this header.
    *   **Encryption Wrapper Stream:** This sits *on top* of the main file data (after the header). Every time DiscUtils reads or writes to the "VHD", this stream automatically encrypts the data going down or decrypts it coming up. DiscUtils just sees plain VHD data.
4.  **Dokany** then takes this "fake plain VHD" provided by DiscUtils and mounts it as a custom file system, making the decrypted files appear.

## Requirements (The Bare Minimum)

*   Windows 7+ (x64)
*   **[Dokany](https://github.com/dokan-dev/dokany/releases/) Installed!** (Just download `DokanSetup.exe` and run it. Easy.)

## Why You Should Probably *Not* Use This (The Ugly Truth)

This is purely a tech demo. It's **full of holes and missing critical stuff**:

*   **☠️ NOT SECURE:** Lacks proper security audits, secure deletion, key management best practices, etc.
*   **💥 Fragile:** No backups of critical container headers (lose this, lose everything inside). Crashes, power loss? Bad news.
*   **🗑️ Space Hog:** The container file (.ofcrypt) only grows bigger (up to 100GB), never shrinks automatically. There's a janky "Optimize" right-click menu to *try* shrinking it manually. Good luck.
*   **⚠️ Stability?** Meh. It *might* work sometimes. Maybe.
*   **🔒 Limited Size:** Max 100GB per container.
*   **👨‍💻 My Skills:** Let's be real, I mostly cobbled this together by asking DeepSeek-R1 for code snippets and hoping it runs. It's held together with digital duct tape.

## How to Actually Try It (If You're Brave/Reckless)

1.  **Install Dokany:** Get it [here](https://github.com/dokan-dev/dokany/releases/). Run `DokanSetup.exe`.
2.  **"Install" OrangeCrypt:** Open a Command Prompt **as Administrator**. Navigate to where `OrangeCrypt.exe` is. Run: `OrangeCrypt.exe /install`
3.  **Lock a Folder:** Right-click on a **test folder** (use junk data!). Choose "Encrypt(ofc)". Follow prompts (set password, wait for copy).
4.  **Open:** Double-click the new `.ofcrypt` file. Enter password. Hopefully, the folder jumps out. Use it (lightly!).
5.  **Close:** Right-click the tray icon -> "Unmount All". If you shut down your system without done this, the daemon is likely to do it for you, hopefully.

## Final Warning (Seriously)

**THIS IS A DEMO. IT'S LIKELY BUGGY AND INSECURE. TREAT IT AS SUCH. DO NOT RELY ON IT FOR ANYTHING REMOTELY IMPORTANT. YOU HAVE BEEN WARNED!**

Built with: C#, [Dokan.Net](https://github.com/dokan-dev/dokan-dotnet), [DiscUtils](https://github.com/LTRData/DiscUtils) (LTRData fork), and a lot of wishful thinking. 🚧🧪