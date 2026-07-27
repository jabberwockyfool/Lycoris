#!/usr/bin/env python3
"""
ykport_gui — a tkinter front-end for ykport (Puni-Puni -> YW3 animation porter).

No third-party dependencies (tkinter ships with standard CPython on Windows).
Fill in the paths, map clips to slots per group, and hit Build — it runs the
same combine + .mtninf + .xc packaging pipeline as the CLI.

    py -3.11 ykport_gui.py        (or double-click ykport_gui.bat)
"""

import os
import json
import threading
import traceback

import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import slots as SLOTS
import ykport


HERE = os.path.dirname(os.path.abspath(__file__))
GROUPS = ["p10", "p20", "p21", "p84"]


def list_mtn2(folder):
    try:
        return sorted(f for f in os.listdir(folder) if f.lower().endswith(".mtn2"))
    except Exception:
        return []


class ClipRow:
    """One clip mapping row inside a group tab."""
    def __init__(self, tab, data=None):
        self.tab = tab
        data = data or {}
        self.frame = ttk.Frame(tab.rows)
        self.frame.pack(fill="x", pady=1)

        self.file = ttk.Combobox(self.frame, width=26, values=list_mtn2(tab.app.clips_dir.get()))
        self.file.set(data.get("file", ""))
        self.slot = ttk.Combobox(self.frame, width=16,
                                 values=list(SLOTS.GROUPS.get(tab.group, {}).keys()))
        self.slot.set(data.get("slot", ""))
        self.name = ttk.Entry(self.frame, width=20)
        self.name.insert(0, data.get("name", ""))
        self.speed = ttk.Entry(self.frame, width=6)
        self.speed.insert(0, str(data.get("speed", tab.app.default_speed.get())))
        rm = ttk.Button(self.frame, text="✕", width=3, command=self.remove)

        for w in (self.file, self.slot, self.name, self.speed, rm):
            w.pack(side="left", padx=2)

    def remove(self):
        self.frame.destroy()
        self.tab.rows_list.remove(self)

    def to_dict(self):
        f = self.file.get().strip()
        if not f:
            return None
        try:
            spd = float(self.speed.get())
        except ValueError:
            spd = 1.0
        return {"file": f, "slot": self.slot.get().strip(),
                "name": self.name.get().strip() or os.path.splitext(f)[0],
                "speed": spd}


class GroupTab:
    def __init__(self, app, nb, group):
        self.app = app
        self.group = group
        self.rows_list = []
        self.frame = ttk.Frame(nb, padding=8)
        nb.add(self.frame, text=group)

        top = ttk.Frame(self.frame)
        top.pack(fill="x", pady=(0, 6))
        ttk.Label(top, text="Donor .xc:").pack(side="left")
        self.donor = ttk.Entry(top, width=48)
        self.donor.pack(side="left", padx=4)
        ttk.Button(top, text="…", width=3,
                   command=lambda: self._browse_donor()).pack(side="left")
        ttk.Label(top, text="  Fallback:").pack(side="left")
        self.fallback = ttk.Combobox(top, width=14,
                                     values=list(SLOTS.GROUPS.get(group, {}).keys()))
        self.fallback.set("idle")
        self.fallback.pack(side="left", padx=4)

        hdr = ttk.Frame(self.frame)
        hdr.pack(fill="x")
        for text, w in (("Clip (.mtn2)", 28), ("Slot", 18), ("Split name", 22), ("Speed", 8), ("", 4)):
            ttk.Label(hdr, text=text, width=w, anchor="w").pack(side="left", padx=2)

        self.rows = ttk.Frame(self.frame)
        self.rows.pack(fill="both", expand=True)

        ttk.Button(self.frame, text="+ Add clip", command=self.add_row).pack(anchor="w", pady=4)

    def _browse_donor(self):
        p = filedialog.askopenfilename(title=f"Donor .xc for {self.group}",
                                       filetypes=[("Level-5 archive", "*.xc"), ("All", "*.*")])
        if p:
            self.donor.delete(0, "end")
            self.donor.insert(0, p)

    def add_row(self, data=None):
        self.rows_list.append(ClipRow(self, data))

    def rescan_files(self):
        vals = list_mtn2(self.app.clips_dir.get())
        for r in self.rows_list:
            r.file["values"] = vals

    def load(self, entries):
        for r in list(self.rows_list):
            r.remove()
        clips, donor, fallback = ykport._group_spec(entries)
        self.donor.delete(0, "end")
        if donor:
            self.donor.insert(0, donor)
        self.fallback.set(fallback or "idle")
        for c in clips:
            self.add_row(c)

    def to_entry(self):
        clips = [r.to_dict() for r in self.rows_list]
        clips = [c for c in clips if c]
        if not clips:
            return None
        donor = self.donor.get().strip()
        if donor:
            return {"donor": donor, "fallback": self.fallback.get().strip() or "idle",
                    "clips": clips}
        return clips


class App(ttk.Frame):
    def __init__(self, master):
        super().__init__(master, padding=10)
        self.pack(fill="both", expand=True)
        master.title("ykport — Puni-Puni → YW3 animation porter")
        master.geometry("880x640")

        self.se_path = tk.StringVar()
        self.model_id = tk.StringVar(value="")
        self.clips_dir = tk.StringVar(value="")
        self.output_dir = tk.StringVar(value=os.path.join(HERE, "out"))
        self.gap = tk.StringVar(value="1")
        self.default_speed = tk.StringVar(value="0.5")

        self._build_form()
        nb = ttk.Notebook(self)
        nb.pack(fill="both", expand=True, pady=6)
        self.tabs = {g: GroupTab(self, nb, g) for g in GROUPS}

        btns = ttk.Frame(self)
        btns.pack(fill="x")
        ttk.Button(btns, text="Load config…", command=self.load_config).pack(side="left")
        ttk.Button(btns, text="Save config…", command=self.save_config).pack(side="left", padx=4)
        ttk.Button(btns, text="Rescan clips", command=self.rescan).pack(side="left")
        self.build_btn = ttk.Button(btns, text="Build ▶", command=self.build)
        self.build_btn.pack(side="right")

        self.log = tk.Text(self, height=11, bg="#1e1e1e", fg="#d4d4d4",
                           insertbackground="#d4d4d4", font=("Consolas", 9))
        self.log.pack(fill="both", expand=False, pady=(6, 0))
        self._log("Fill in the fields, map clips to slots per group, then Build.\n"
                  "studio_eleven is auto-detected; set it only if that fails.")

    def _build_form(self):
        form = ttk.Frame(self)
        form.pack(fill="x")

        def row(r, label, var, browse=None, width=60):
            ttk.Label(form, text=label, width=14, anchor="w").grid(row=r, column=0, sticky="w", pady=2)
            e = ttk.Entry(form, textvariable=var, width=width)
            e.grid(row=r, column=1, sticky="w")
            if browse:
                ttk.Button(form, text="…", width=3, command=browse).grid(row=r, column=2, padx=4)
            return e

        row(0, "Model ID", self.model_id, width=24)
        row(1, "Clips dir", self.clips_dir, browse=lambda: self._pick_dir(self.clips_dir, self.rescan))
        row(2, "Output dir", self.output_dir, browse=lambda: self._pick_dir(self.output_dir))
        row(3, "studio_eleven", self.se_path, browse=lambda: self._pick_dir(self.se_path))

        small = ttk.Frame(form)
        small.grid(row=4, column=1, sticky="w", pady=2)
        ttk.Label(small, text="Gap").pack(side="left")
        ttk.Spinbox(small, from_=0, to=30, width=4, textvariable=self.gap).pack(side="left", padx=(4, 16))
        ttk.Label(small, text="Default speed").pack(side="left")
        ttk.Entry(small, width=6, textvariable=self.default_speed).pack(side="left", padx=4)

    # ---- helpers ----
    def _pick_dir(self, var, after=None):
        p = filedialog.askdirectory(title="Choose folder")
        if p:
            var.set(p)
            if after:
                after()

    def rescan(self):
        for t in self.tabs.values():
            t.rescan_files()

    def _log(self, msg):
        self.log.insert("end", msg + "\n")
        self.log.see("end")
        self.log.update_idletasks()

    def to_cfg(self):
        groups = {}
        for g, tab in self.tabs.items():
            e = tab.to_entry()
            if e is not None:
                groups[g] = e
        cfg = {
            "model_id": self.model_id.get().strip(),
            "clips_dir": self.clips_dir.get().strip(),
            "output_dir": self.output_dir.get().strip(),
            "gap": int(self.gap.get() or 1),
        }
        if self.se_path.get().strip():
            cfg["studio_eleven"] = self.se_path.get().strip()
        cfg["groups"] = groups
        return cfg

    def load_config(self):
        p = filedialog.askopenfilename(title="Load config",
                                       filetypes=[("JSON", "*.json"), ("All", "*.*")])
        if not p:
            return
        try:
            with open(p, "r", encoding="utf-8") as fh:
                cfg = json.load(fh)
        except Exception as ex:
            messagebox.showerror("Load config", str(ex))
            return
        self.model_id.set(cfg.get("model_id", ""))
        self.clips_dir.set(cfg.get("clips_dir", ""))
        self.output_dir.set(cfg.get("output_dir", os.path.join(HERE, "out")))
        self.gap.set(str(cfg.get("gap", 1)))
        self.se_path.set(cfg.get("studio_eleven") or "")
        for g, tab in self.tabs.items():
            tab.load(cfg.get("groups", {}).get(g, []))
        self._log(f"Loaded {os.path.basename(p)}")

    def save_config(self):
        p = filedialog.asksaveasfilename(title="Save config", defaultextension=".json",
                                         filetypes=[("JSON", "*.json")])
        if not p:
            return
        with open(p, "w", encoding="utf-8") as fh:
            json.dump(self.to_cfg(), fh, indent=2, ensure_ascii=False)
        self._log(f"Saved {os.path.basename(p)}")

    def build(self):
        cfg = self.to_cfg()
        if not cfg["clips_dir"]:
            messagebox.showwarning("Build", "Set the clips dir first."); return
        if not cfg["groups"]:
            messagebox.showwarning("Build", "Add at least one clip in a group."); return
        self.build_btn.config(state="disabled")
        self._log("\n" + "=" * 60)
        threading.Thread(target=self._build_worker, args=(cfg,), daemon=True).start()

    def _build_worker(self, cfg):
        try:
            se = ykport.load_studio_eleven(cfg.get("studio_eleven"))
            ykport.build_from_cfg(cfg, HERE, se, log=self._log)
            self._log("✓ Build finished. Test a produced .xc in-game.")
        except Exception:
            self._log("✗ ERROR:\n" + traceback.format_exc())
        finally:
            self.build_btn.config(state="normal")


if __name__ == "__main__":
    root = tk.Tk()
    try:
        ttk.Style().theme_use("vista")
    except Exception:
        pass
    App(root)
    root.mainloop()
