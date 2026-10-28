# Install a Josour server, step by step

*[النسخة العربية](install-server.ar.md)*

This guide is for someone who has **never run a server**. You will rent a small server on the internet, give it a
name, paste **one command**, and answer a few questions. The command does everything else: the security settings,
the software, the encryption certificate, the passwords and the daily backups.

It takes about **30 minutes**, most of it waiting. You pay only for the server (about 4–6 US dollars a month) and the
domain name (about 10 dollars a year) — Josour itself is free.

> **Before you start, read [the host's responsibility](../README.md#read-this-before-you-host).** When you share your
> connection, the sites someone else visits see *your* address.

The manual version of every step, with the reasons behind it, is in [self-hosting.md](self-hosting.md). You do not
need it to follow this page.

---

## What you need

| | |
|---|---|
| **A server** ("VPS") | Rented from any hosting company. Choose **Ubuntu 24.04**, **2 GB of memory**, 1 CPU. Step 1 |
| **A domain name** | Any name you own, bought from any registrar (Namecheap, GoDaddy, Cloudflare, …). You will use a part of it, such as `josour.yourname.com`. Step 2 |
| **A computer** | Windows 10/11 or a Mac, to type the commands from. Nothing is installed on it for the server |
| **An email address** | For the administrator's account, and for warnings about the certificate |

Throughout this page, `josour.example.com` stands for your server's name and `203.0.113.10` for its address.
**Replace them with yours.**

---

## Step 1 — Rent the server

At the hosting company of your choice (Hetzner, DigitalOcean, Vultr, Linode, Contabo, OVH, … any of them works):

1. Create a new server (it may be called a *VPS*, a *droplet*, an *instance* or a *cloud server*).
2. **Operating system:** Ubuntu **24.04** (22.04 and Debian 12 also work).
3. **Size:** the smallest plan with **2 GB of memory** (1 GB works, but is slower to install).
4. **Location:** somewhere between the two people who will use it. For someone abroad using a connection at home,
   pick the region closest to home. For Egypt ↔ Saudi Arabia, for example, a Gulf region is faster than Europe.
5. **Login:** choose a **root password** (or an SSH key, if you know what that is). Write the password down.
6. Create it. After a minute the control panel shows its **IPv4 address**, four numbers like `203.0.113.10`.
   Write it down.

> Some companies make you log in as a user called `ubuntu` or `admin` instead of `root`. That is fine: use that name
> wherever this page says `root`.

---

## Step 2 — Give the server a name

At the company where you bought your domain, open the **DNS settings** of the domain (sometimes *DNS records*,
*Zone editor* or *Advanced DNS*) and **add a record**:

| Type | Name (Host) | Value (Points to) | TTL |
|---|---|---|---|
| `A` | `josour` | `203.0.113.10` — your server's address | Automatic (or 5 minutes) |

With the name `josour`, the server is reachable as `josour.<your domain>`, for example `josour.example.com`.

- **Only an `A` record.** If there is already an `AAAA` record for the same name, delete it.
- **On Cloudflare**, set the record to **DNS only** (a grey cloud, not orange).
- A new record usually works within 5–30 minutes. The installer checks it for you and waits if it is not ready.

---

## Step 3 — Open the ports (only if your hosting company has a firewall)

Some companies (AWS, Oracle Cloud, Google Cloud, Azure, and sometimes Hetzner) have a **firewall** or *security group*
in their control panel that blocks everything by default. If yours has one, allow these **incoming** ports:

| Protocol | Port | What for |
|---|---|---|
| TCP | 22 | Connecting to the server (SSH) |
| TCP | 80 | Getting the certificate |
| TCP | 443 | The app and the dashboard |
| UDP | 443 | The same, faster |
| TCP | 8443 | The relay: what makes a session work between any two networks |

If your company has no such setting, skip this step. The installer sets up the server's own firewall itself.

---

## Step 4 — Connect to the server

**On Windows:** open the Start menu, type **PowerShell** and open it. **On a Mac:** open **Terminal** (in
Applications → Utilities).

Type this, with your server's address, and press Enter:

```bash
ssh root@203.0.113.10
```

- The first time, it asks *"Are you sure you want to continue connecting?"* — type `yes` and press Enter.
- Then it asks for the password. **Nothing appears on the screen while you type it**: that is normal. Type it (or
  paste it with a right-click) and press Enter.

You are in when the line ends with something like `root@your-server:~#`. Every command from now on is typed there.

> **Copying and pasting:** in PowerShell and Terminal, paste with **right-click** (Windows) or **Cmd+V** (Mac).
> Ctrl+V may not work.

---

## Step 5 — Install Josour

Paste these two lines and press Enter:

```bash
curl -fsSL https://raw.githubusercontent.com/mo-shaker/josour/main/deploy/install.sh -o josour-install.sh
sudo bash josour-install.sh
```

It asks five questions. Type the answer and press Enter. Where a value is shown in `[brackets]`, pressing Enter
accepts it.

| Question | What to answer |
|---|---|
| **The server's name** | The name from Step 2, e.g. `josour.example.com` — without `https://` |
| **Your email address** | Your email. Let's Encrypt, which issues the free certificate, writes there only if the certificate has a problem |
| **Administrator's email** | The email you will sign in with, to the dashboard and the app. Press Enter to use the same one |
| **Administrator's name** | Your name, as other people will see it |
| **Password** (twice) | The administrator's password, **at least 8 characters**. Nothing appears while you type: normal |

Then it works on its own for **5 to 15 minutes**, showing each step and ending each one with `done`:

```
[1/8] Checking that josour.example.com points at this server
[2/8] Preparing the system (updates, firewall, automatic security updates)
[3/8] Installing Docker
[4/8] Fetching Josour
[5/8] Writing the settings
[6/8] Starting the server (the first start builds it: allow 5 to 10 minutes)
[7/8] The administrator account
[8/8] A first backup, and tidying up
```

It ends with **"Josour is installed"** and a summary: the address of the dashboard, the address to type in the app,
and where the settings are kept. **Take a photo of that summary, or copy it somewhere.**

**If it stops with an error**, the message says what is wrong — most often the name from Step 2 is not ready yet,
or a port from Step 3 is closed. Fix that and **run `sudo bash josour-install.sh` again**: it is safe to run as many
times as you need, and it picks up where it stopped.

**Keep a copy of the settings file.** It holds the server's passwords, and without it a backup is much harder to
restore. From **your computer** (a new PowerShell or Terminal window, not the one connected to the server):

```bash
scp root@203.0.113.10:/opt/josour/deploy/.env josour-settings-backup.txt
```

Keep that file somewhere private, like a password manager. Do not send it to anyone.

---

## Step 6 — Sign in to the dashboard

Open **`https://josour.example.com/admin`** in your browser and sign in with the administrator's email and password.
The dashboard is in Arabic; the link at the top switches it to English.

Then, once:

1. **Turn on two-factor authentication.** *My account* → set up an authenticator app (Google Authenticator,
   Microsoft Authenticator, 1Password, Bitwarden, …): scan the code with your phone and type the six digits it shows.
   From now on, signing in to the dashboard also asks for a code from your phone.
2. **Decide who may create an account** — *Settings* → *Registration from the app*:
   - **With administrator approval** (the default): anyone with the app can sign up, and you approve each person
     under *Registrations*. The right choice for most people.
   - **Closed**: only you create accounts, under *Users*. The tightest choice for a small private group.
   - **Open**: anyone can sign up and use it at once. Not recommended.

---

## Step 7 — Install the app on each computer

Each person — the one who shares a connection (the **host**) and the one who uses it (the **guest**) — installs the
app. Download it:

| Computer | Download |
|---|---|
| **Windows** 10/11 | [Josour-win-x64.exe](https://github.com/mo-shaker/josour/releases/latest/download/Josour-win-x64.exe) |
| **Mac** with Apple silicon (M1, M2, M3, M4…) | [Josour-macos-arm64.dmg](https://github.com/mo-shaker/josour/releases/latest/download/Josour-macos-arm64.dmg) |
| **Mac** with an Intel processor (older Macs) | [Josour-macos-x64.dmg](https://github.com/mo-shaker/josour/releases/latest/download/Josour-macos-x64.dmg) |

(Which Mac? Apple menu → *About This Mac*: "Chip: Apple M…" is Apple silicon; "Processor: Intel…" is Intel.)
All versions are on the [Releases page](https://github.com/mo-shaker/josour/releases).

**Windows:** put the file somewhere permanent, for example a folder `C:\Josour`, and double-click it. Windows says
*"Windows protected your PC"* the first time: click **More info**, then **Run anyway**. (The app is not signed with a
paid certificate; that is a [deliberate decision](decisions/0011-no-code-signing-certificate.md).)

**Mac:** open the `.dmg` and drag **Josour** onto **Applications**. The first time, macOS refuses to open it:
- **macOS 15 and later:** click **Done**, then go to **System Settings → Privacy & Security**, scroll down, click
  **Open Anyway** next to Josour, and confirm with your password.
- **macOS 14 and earlier:** **right-click** Josour in Applications → **Open** → **Open**.

**When the app first opens** it asks for:

1. **The server's address:** `https://josour.example.com`.
2. **Sign in** (the administrator), or **Create account** for everyone else. With approval mode, a new account waits
   until you approve it in the dashboard under **Registrations**.

---

## Step 8 — Connect the two people

People share a connection only with their **contacts**. In the app, one person opens the **Contacts** tab, clicks
**Invite** and types the other person's **email address**; the other accepts the invitation in their own app. (No email is actually sent; the
invitation appears inside the app.)

> **For a small group that all trust each other**, you can link everyone at once instead. On the server:
> `cd /opt/josour/deploy && docker compose exec api python manage.py seed-contacts --all-active`

---

## Step 9 — Try it

Best with the two computers on **two different networks**, for example the host on home Wi-Fi and the guest on a
phone's hotspot.

1. On **both** computers, open `https://api.ipify.org` in a browser and note the number. They must be different.
2. **Host:** open the **Share my connection** tab and turn sharing on.
3. **Guest:** in the **Contacts** tab, click **Use their connection** next to the host and choose how long.
4. **Host:** a window asks for permission. Read it and **Accept**.
5. **Guest:** a check page opens by itself and shows the host's address. Reload `https://api.ipify.org` in any
   browser: it now shows the **host's** number. That is the whole product.
6. **End** the session from either side. On the guest, `https://api.ipify.org` shows its own number again.

---

## Keeping it running

### Updating to a new version

Connect to the server (Step 4) and run:

```bash
sudo bash /opt/josour/deploy/install.sh
```

It takes a backup, downloads the newest release, rebuilds and restarts — without asking anything and without
changing your settings. Do it when nobody is in a session: a session running during the update ends. Then install
the new app on the computers too (Step 7); the app keeps its settings and sign-in.

### Backups

The server writes a backup of its database **every day** into `/opt/josour/deploy/backups`, and keeps two weeks of
them. A backup on the same server does not survive the server, so copy them to your computer from time to time
(from your computer, not from the server):

```bash
scp -r root@203.0.113.10:/opt/josour/deploy/backups josour-backups
```

To restore one, or to move to a new server: [self-hosting.md, part 7](self-hosting.md#7-running-it).

---

## If something does not work

| What you see | What to do |
|---|---|
| The installer says the name *does not point at this server* | The DNS record from Step 2 is missing, has a typo, or is not active yet. Check it, wait 10 minutes, press Enter to check again |
| The installer says *another program already uses port 80/443* | The server is not fresh (it already runs a website). Use a new, empty server |
| The installer says *no certificate yet* | Port 80 or 443 is closed in the hosting company's firewall (Step 3), or the DNS record is not ready. Fix it and run the installer again |
| `ssh` says *Connection refused* or *timed out* | Wrong address, the server is still starting, or port 22 is closed in the company's firewall |
| `ssh` says *Permission denied* | Wrong password, or the company wants another user name than `root` (Step 1) |
| `ssh` warns that the *host identification has changed* | Normal after reinstalling the server: run `ssh-keygen -R 203.0.113.10` on your computer, then connect again |
| The app says the server address is wrong | Open `https://josour.example.com/healthz` in a browser. It should show `"product":"josour"`. If not, run the installer again and read its messages |
| The app says the account is *waiting for approval* | Approve it in the dashboard, under **Registrations** |
| The host does not appear for the guest | They are not contacts yet (Step 8), or the host has not turned sharing on (*Share my connection* tab) |
| A session never connects | Port **8443** is closed in the hosting company's firewall (Step 3) |
| The guest has no internet after the app crashed | Open Josour again: it puts the settings back. Or turn the proxy off by hand: Windows *Settings → Network & internet → Proxy*; Mac *System Settings → Network → the connection → Details → Proxies* |
| Lost the phone with the authenticator app | See [self-hosting.md, part 8](self-hosting.md#8-if-it-does-not-work) |

The installer writes everything it did to `/var/log/josour-install.log` on the server. If you ask someone for help,
that file is what they need — but look through it first, and remove anything you would not share.
