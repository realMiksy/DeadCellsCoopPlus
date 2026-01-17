
<h1>Dead Cells Multiplayer Mod</h1>

**DeadCellsMultiplayerMod** is a **multiplayer / co-op mod for Dead Cells**, built using the **Dead Cells Core Modding API (DCCM)**.

The mod adds **co-op / multiplayer gameplay** via a **local or virtual network**:  
one player hosts a server, another connects — and both players can **play through levels together in real time**.

> ⚠️ The project is currently in development. Many core multiplayer systems are implemented, but full synchronization is still a work in progress.

---

## 🎮 Features

- ✅ Real-time synchronization between two players  
- ✅ Local TCP-based multiplayer server  
- ✅ Host / Client architecture    
- ✅ Automatic game start for connected clients  
- 🧪 Experimental multiplayer gameplay  

---

## 🧰 Requirements

- **Dead Cells (PC)**
- **Dead Cells Core Modding API (DCCM)**
- Local network or virtual LAN software (for online play)

---

## 📦 Installation

### 1️⃣ Install Dead Cells Core Modding API (DCCM)

Download the latest version of DCCM from the official repository:  
https://github.com/dead-cells-core-modding/core

Follow the installation instructions provided on the repository page.
P.S. You can download the **non-MDK version** if you don’t plan to create mods yourself.
---

### 2️⃣ Install DeadCellsMultiplayerMod

1. Navigate to your **DCCM directory**
2. Create a folder named `mods` (if it doesn’t exist)
3. Extract the **DeadCellsMultiplayerMod** folder into the `mods` directory

Example:
```
Your game path/
 └──coremod/
    └── mods/
        └── DeadCellsMultiplayerMod/
```

---

### 3️⃣ Run the game via DCCM

Start **Dead Cells** using **DCCM**.  
On the first launch, required configuration files will be generated automatically.

---

## 🕹️ How to Play (Multiplayer)

1. Launch the game via **DCCM**
2. Click **Play Multiplayer**
3. Choose **Host** or **Join**
4. Enter **IP address** and **port**
5. When the host starts the game, the client will automatically join the session

🌐 **For online play**, use one of the following virtual LAN tools:
- Hamachi  
- Radmin VPN  
- ZeroTier  

---

## 🧪 Development Status / TODO

- [x] Create second player ghost  
- [x] Synchronize new game world data  
- [x] Add player ghost animations  
- [x] Improve ghost animation quality  
- [ ] Synchronize level generation  
- [ ] Synchronize enemies  
- [ ] Synchronize bosses  
- [ ] Implement death handling for ghost player  

---

## 📜 Credits

- **Dead Cells Core Modding API (DCCM)**  
  https://github.com/dead-cells-core-modding/core

---

## ⭐ Support the Project

If you find this project interesting:
- ⭐ Star the repository  
- 🍴 Fork the project and experiment  

Every bit of feedback helps improve multiplayer support for **Dead Cells**.

google-site-verification=MP6XD0MymtlSquKtxsBBLDFj0oE84lczF

<!--
Keywords: Dead Cells multiplayer mod, Dead Cells co-op mod, Dead Cells online, DCCM mod, Dead Cells TCP multiplayer
-->
