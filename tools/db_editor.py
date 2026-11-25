# -*- coding: utf-8 -*-
"""
Ground War Tanks - Редактор базы данных аккаунта.

Графический редактор SQLite-базы эмулятора (gwt_server.db):
  - Валюта (серебро, золото, опыт), уровень, VIP / премиум
  - Танки: просмотр, выдача любого танка из игровых таблиц, удаление, "выдать все"
  - Экипаж: просмотр и удаление

Запуск: gwt_db_editor.bat (или python tools/db_editor.py)

Совет: правки применяются сразу в БД. Чтобы изменения подхватились в игре,
переподключитесь (выйдите в ангар заново) или перезапустите клиент.
"""
import os
import sys
import json
import random
import sqlite3
import tkinter as tk
from tkinter import ttk, messagebox

# ---------------------------------------------------------------------------
# Поиск путей
# ---------------------------------------------------------------------------
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)  # emuserverGWT

DB_CANDIDATES = [
    os.path.join(ROOT, "GwtEmulator.App", "gwt_server.db"),
    os.path.join(ROOT, "gwt_server.db"),
]
TABLES_JSON = os.path.join(ROOT, "GwtEmulator.Server", "GameTables", "Data", "client_game_tables.json")


def find_db():
    for p in DB_CANDIDATES:
        if os.path.isfile(p):
            return p
    # fall back to the first candidate even if missing (so the path shows in the UI)
    return DB_CANDIDATES[0]


def load_tank_table():
    """Возвращает список словарей танков из игровых таблиц (id, name, modules...)."""
    try:
        with open(TABLES_JSON, encoding="utf-8") as f:
            data = json.load(f)
        return data.get("Tanks", [])
    except Exception as e:
        print("Не удалось прочитать игровые таблицы:", e)
        return []


def gen_guid():
    return random.randint(1, 9_000_000_000_000_000_000)


# ---------------------------------------------------------------------------
# Приложение
# ---------------------------------------------------------------------------
class DbEditor(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("GWT — Редактор БД аккаунта")
        self.geometry("960x620")
        self.minsize(820, 540)

        self.db_path = find_db()
        self.tanks_table = load_tank_table()
        self.tank_by_id = {int(t["Id"]): t for t in self.tanks_table}
        # Отображаемое имя -> id
        self.tank_display = {}
        for t in sorted(self.tanks_table, key=lambda x: int(x["Id"])):
            label = f'{t["Id"]:>4}  {t.get("Name","?")}  (Tier {t.get("Tier","?")})'
            self.tank_display[label] = int(t["Id"])

        self.accounts = []        # list of (guid, name)
        self.current_guid = None

        self._build_ui()
        self._load_accounts()

    # ----------------------------- БД --------------------------------------
    def connect(self):
        conn = sqlite3.connect(self.db_path, timeout=5)
        conn.execute("PRAGMA journal_mode=WAL")
        conn.execute("PRAGMA busy_timeout=4000")
        return conn

    # ----------------------------- UI --------------------------------------
    def _build_ui(self):
        top = ttk.Frame(self, padding=8)
        top.pack(fill="x")

        ttk.Label(top, text="Аккаунт:").pack(side="left")
        self.acc_combo = ttk.Combobox(top, state="readonly", width=46)
        self.acc_combo.pack(side="left", padx=6)
        self.acc_combo.bind("<<ComboboxSelected>>", lambda e: self._on_account_selected())
        ttk.Button(top, text="Обновить", command=self._load_accounts).pack(side="left", padx=4)

        self.db_label = ttk.Label(top, text="", foreground="#666")
        self.db_label.pack(side="right")

        nb = ttk.Notebook(self)
        nb.pack(fill="both", expand=True, padx=8, pady=6)

        self.tab_acc = ttk.Frame(nb, padding=10)
        self.tab_tanks = ttk.Frame(nb, padding=10)
        self.tab_crew = ttk.Frame(nb, padding=10)
        nb.add(self.tab_acc, text="Валюта и аккаунт")
        nb.add(self.tab_tanks, text="Танки")
        nb.add(self.tab_crew, text="Экипаж")

        self._build_account_tab()
        self._build_tanks_tab()
        self._build_crew_tab()

        self.status = ttk.Label(self, text="", anchor="w", relief="sunken", padding=4)
        self.status.pack(fill="x", side="bottom")
        self.db_label.config(text="БД: " + self.db_path)

    def _build_account_tab(self):
        f = self.tab_acc
        self.acc_vars = {}
        rows = [
            ("Silver", "Серебро"),
            ("Gold", "Золото"),
            ("Experience", "Свободный опыт"),
            ("Level", "Уровень"),
            ("BattleCap", "Боевая мощь"),
            ("CasernCapacity", "Вместимость казармы"),
        ]
        for i, (col, label) in enumerate(rows):
            ttk.Label(f, text=label + ":").grid(row=i, column=0, sticky="e", padx=6, pady=5)
            var = tk.StringVar()
            ent = ttk.Entry(f, textvariable=var, width=22)
            ent.grid(row=i, column=1, sticky="w", padx=6, pady=5)
            self.acc_vars[col] = var

        # быстрые кнопки
        qf = ttk.Frame(f)
        qf.grid(row=0, column=2, rowspan=3, padx=20, sticky="n")
        ttk.Button(qf, text="Серебро = 1 млрд", command=lambda: self.acc_vars["Silver"].set("1000000000")).pack(fill="x", pady=2)
        ttk.Button(qf, text="Золото = 1 млрд", command=lambda: self.acc_vars["Gold"].set("1000000000")).pack(fill="x", pady=2)
        ttk.Button(qf, text="Опыт = 1 млрд", command=lambda: self.acc_vars["Experience"].set("1000000000")).pack(fill="x", pady=2)

        self.vip_var = tk.IntVar()
        self.prem_var = tk.IntVar()
        ttk.Checkbutton(f, text="VIP активен (на 1 год)", variable=self.vip_var).grid(row=6, column=0, columnspan=2, sticky="w", padx=6, pady=4)
        ttk.Checkbutton(f, text="Премиум активен (на 1 год)", variable=self.prem_var).grid(row=7, column=0, columnspan=2, sticky="w", padx=6, pady=2)

        ttk.Button(f, text="💾  Сохранить аккаунт", command=self._save_account).grid(row=8, column=0, columnspan=2, sticky="w", padx=6, pady=14)

    def _build_tanks_tab(self):
        f = self.tab_tanks
        left = ttk.Frame(f)
        left.pack(side="left", fill="both", expand=True)

        cols = ("guid", "vid", "name", "gun", "exp")
        self.tank_tree = ttk.Treeview(left, columns=cols, show="headings", height=16)
        for c, w, txt in [("guid", 150, "TankGUID"), ("vid", 50, "ID"), ("name", 230, "Название"),
                          ("gun", 60, "Пушка"), ("exp", 90, "Опыт")]:
            self.tank_tree.heading(c, text=txt)
            self.tank_tree.column(c, width=w, anchor="w")
        self.tank_tree.pack(side="left", fill="both", expand=True)
        sb = ttk.Scrollbar(left, orient="vertical", command=self.tank_tree.yview)
        sb.pack(side="left", fill="y")
        self.tank_tree.configure(yscrollcommand=sb.set)

        right = ttk.Frame(f, padding=(12, 0))
        right.pack(side="left", fill="y")
        ttk.Label(right, text="Выдать танк:").pack(anchor="w")
        self.grant_combo = ttk.Combobox(right, state="readonly", width=40,
                                        values=list(self.tank_display.keys()))
        self.grant_combo.pack(anchor="w", pady=4)
        ttk.Button(right, text="➕  Выдать выбранный", command=self._grant_selected_tank).pack(fill="x", pady=3)
        ttk.Separator(right, orient="horizontal").pack(fill="x", pady=8)
        ttk.Button(right, text="🎁  Выдать ВСЕ танки", command=self._grant_all_tanks).pack(fill="x", pady=3)
        ttk.Button(right, text="🗑  Удалить выбранный", command=self._delete_selected_tank).pack(fill="x", pady=3)
        ttk.Button(right, text="🔄  Обновить список", command=self._load_tanks).pack(fill="x", pady=3)
        ttk.Label(right, text="(после выдачи переподключитесь\nв ангар, чтобы танк появился)",
                  foreground="#666").pack(anchor="w", pady=10)

    def _build_crew_tab(self):
        f = self.tab_crew
        cols = ("guid", "crewid", "lv", "tank")
        self.crew_tree = ttk.Treeview(f, columns=cols, show="headings", height=16)
        for c, w, txt in [("guid", 160, "CrewGuid"), ("crewid", 80, "CrewId"),
                          ("lv", 60, "Уровень"), ("tank", 160, "TankGuid (0=свободен)")]:
            self.crew_tree.heading(c, text=txt)
            self.crew_tree.column(c, width=w, anchor="w")
        self.crew_tree.pack(side="left", fill="both", expand=True)
        right = ttk.Frame(f, padding=(12, 0))
        right.pack(side="left", fill="y")
        ttk.Button(right, text="🗑  Удалить выбранного", command=self._delete_selected_crew).pack(fill="x", pady=3)
        ttk.Button(right, text="🔄  Обновить", command=self._load_crew).pack(fill="x", pady=3)

    # --------------------------- Загрузка ----------------------------------
    def _load_accounts(self):
        if not os.path.isfile(self.db_path):
            self._set_status("База не найдена: " + self.db_path)
            return
        try:
            conn = self.connect()
            rows = conn.execute(
                "SELECT PlayerGUID, PlayerName, Silver, Gold FROM Accounts ORDER BY LastLogin DESC"
            ).fetchall()
            conn.close()
        except Exception as e:
            self._set_status("Ошибка чтения аккаунтов: " + str(e))
            return
        self.accounts = [(r[0], r[1]) for r in rows]
        labels = [f"{r[1]}  (GUID {r[0]})  S:{r[2]:,} G:{r[3]:,}" for r in rows]
        self.acc_combo["values"] = labels
        if labels:
            self.acc_combo.current(0)
            self._on_account_selected()
            self._set_status(f"Загружено аккаунтов: {len(labels)}")
        else:
            self._set_status("В базе нет аккаунтов. Запустите игру хотя бы раз.")

    def _on_account_selected(self):
        idx = self.acc_combo.current()
        if idx < 0 or idx >= len(self.accounts):
            return
        self.current_guid = self.accounts[idx][0]
        self._load_account_fields()
        self._load_tanks()
        self._load_crew()

    def _load_account_fields(self):
        conn = self.connect()
        row = conn.execute(
            "SELECT Silver,Gold,Experience,Level,BattleCap,CasernCapacity,IsVip,IsPremium "
            "FROM Accounts WHERE PlayerGUID=?", (self.current_guid,)).fetchone()
        conn.close()
        if not row:
            return
        keys = ["Silver", "Gold", "Experience", "Level", "BattleCap", "CasernCapacity"]
        for i, k in enumerate(keys):
            self.acc_vars[k].set(str(row[i]))
        self.vip_var.set(int(row[6]))
        self.prem_var.set(int(row[7]))

    def _load_tanks(self):
        for it in self.tank_tree.get_children():
            self.tank_tree.delete(it)
        if self.current_guid is None:
            return
        conn = self.connect()
        rows = conn.execute(
            "SELECT TankGUID,VehicleId,MainGunID,TankExp FROM Tanks WHERE PlayerGUID=? ORDER BY CarportIndex",
            (self.current_guid,)).fetchall()
        conn.close()
        for r in rows:
            name = self.tank_by_id.get(int(r[1]), {}).get("Name", "?")
            self.tank_tree.insert("", "end", values=(r[0], r[1], name, r[2], r[3]))

    def _load_crew(self):
        for it in self.crew_tree.get_children():
            self.crew_tree.delete(it)
        if self.current_guid is None:
            return
        conn = self.connect()
        rows = conn.execute(
            "SELECT CrewGuid,CrewId,CurLv,TankGuid FROM Crew WHERE PlayerGUID=?",
            (self.current_guid,)).fetchall()
        conn.close()
        for r in rows:
            self.crew_tree.insert("", "end", values=r)

    # --------------------------- Сохранение --------------------------------
    def _save_account(self):
        if self.current_guid is None:
            return
        try:
            vals = {}
            for k in ["Silver", "Gold", "Experience", "Level", "BattleCap", "CasernCapacity"]:
                vals[k] = int(self.acc_vars[k].get().replace(" ", "").replace(",", ""))
        except ValueError:
            messagebox.showerror("Ошибка", "Числовые поля должны содержать только числа.")
            return
        from datetime import datetime, timedelta
        expire = (datetime.utcnow() + timedelta(days=365)).strftime("%Y-%m-%d %H:%M:%S")
        conn = self.connect()
        conn.execute(
            "UPDATE Accounts SET Silver=?,Gold=?,Experience=?,Level=?,BattleCap=?,CasernCapacity=?,"
            "IsVip=?,VipExpireDate=?,IsPremium=?,PremiumExpireDate=? WHERE PlayerGUID=?",
            (vals["Silver"], vals["Gold"], vals["Experience"], vals["Level"], vals["BattleCap"],
             vals["CasernCapacity"], self.vip_var.get(), expire, self.prem_var.get(), expire,
             self.current_guid))
        conn.commit()
        conn.close()
        self._set_status("Аккаунт сохранён. Переподключитесь в ангар, чтобы увидеть изменения.")
        self._load_accounts_keep_selection()

    def _load_accounts_keep_selection(self):
        guid = self.current_guid
        self._load_accounts()
        for i, (g, _n) in enumerate(self.accounts):
            if g == guid:
                self.acc_combo.current(i)
                self.current_guid = guid
                break

    def _insert_tank(self, conn, vid):
        t = self.tank_by_id.get(int(vid))
        if not t:
            return False
        # уже есть такой танк?
        ex = conn.execute("SELECT COUNT(1) FROM Tanks WHERE PlayerGUID=? AND VehicleId=?",
                          (self.current_guid, vid)).fetchone()[0]
        if ex:
            return False
        ci = conn.execute("SELECT COALESCE(MAX(CarportIndex),-1)+1 FROM Tanks WHERE PlayerGUID=?",
                          (self.current_guid,)).fetchone()[0]
        hp = max(1, int(t.get("MaxHp", 1000)))
        currency_type = 2 if int(t.get("Currency", 1)) == 2 else 0
        conn.execute(
            "INSERT INTO Tanks (TankGUID,PlayerGUID,CarportIndex,VehicleId,HullID,UnderpanID,TurretID,"
            "MainGunID,EngineID,FueltankID,CartridgeID,RadioStationID,GaugeID,TankHp,TankExp,TankCurHp,"
            "IsBattleTank,BattleSlot,TankCurrencyType) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,0,0,?)",
            (gen_guid(), self.current_guid, ci, int(t["Id"]), int(t["HullId"]), int(t["UnderpanId"]),
             int(t["TurretId"]), int(t["MainGunId"]), int(t["EngineId"]), int(t["FueltankId"]),
             int(t["CartridgeId"]), int(t["RadioStationId"]), int(t["GaugeId"]), hp, 9_999_999, hp,
             currency_type))
        return True

    def _grant_selected_tank(self):
        if self.current_guid is None:
            return
        label = self.grant_combo.get()
        if not label:
            self._set_status("Выберите танк из списка.")
            return
        vid = self.tank_display[label]
        conn = self.connect()
        ok = self._insert_tank(conn, vid)
        conn.commit()
        conn.close()
        self._load_tanks()
        self._set_status("Танк выдан. Переподключитесь в ангар." if ok else "Этот танк уже есть у аккаунта.")

    def _grant_all_tanks(self):
        if self.current_guid is None:
            return
        if not messagebox.askyesno("Подтверждение", "Выдать ВСЕ 210 танков этому аккаунту?"):
            return
        conn = self.connect()
        n = 0
        for vid in self.tank_by_id:
            if self._insert_tank(conn, vid):
                n += 1
        conn.commit()
        conn.close()
        self._load_tanks()
        self._set_status(f"Выдано новых танков: {n}. Переподключитесь в ангар.")

    def _delete_selected_tank(self):
        sel = self.tank_tree.selection()
        if not sel:
            return
        guid = self.tank_tree.item(sel[0])["values"][0]
        conn = self.connect()
        conn.execute("DELETE FROM Tanks WHERE TankGUID=?", (guid,))
        conn.execute("UPDATE Crew SET TankGuid=0, IsEquipped=0 WHERE TankGuid=?", (guid,))
        conn.commit()
        conn.close()
        self._load_tanks()
        self._set_status("Танк удалён.")

    def _delete_selected_crew(self):
        sel = self.crew_tree.selection()
        if not sel:
            return
        guid = self.crew_tree.item(sel[0])["values"][0]
        conn = self.connect()
        conn.execute("DELETE FROM Crew WHERE CrewGuid=?", (guid,))
        conn.commit()
        conn.close()
        self._load_crew()
        self._set_status("Экипаж удалён.")

    def _set_status(self, text):
        self.status.config(text=text)


if __name__ == "__main__":
    app = DbEditor()
    app.mainloop()
