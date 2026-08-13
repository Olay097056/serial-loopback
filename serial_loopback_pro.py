"""
Serial Loopback Test PRO
10x better than the original — by Claude
"""

import customtkinter as ctk
import threading
import time
import serial
import serial.tools.list_ports
import random
import csv
import os
from datetime import datetime
from collections import deque
import tkinter as tk
from tkinter import filedialog, messagebox

import matplotlib
matplotlib.use("TkAgg")
from matplotlib.figure import Figure
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg
import matplotlib.animation as animation

# ── Theme ──────────────────────────────────────────────────────────────────────
ctk.set_appearance_mode("dark")
ctk.set_default_color_theme("blue")

BAUD_RATES = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600]
MAX_HISTORY = 60  # seconds of graph data

# ── Data model per port ────────────────────────────────────────────────────────
class PortTest:
    def __init__(self, port):
        self.port = port
        self.running = False
        self.thread = None
        self.results = {}          # baud -> {sent, errors, latency_ms}
        self.live_throughput = deque([0]*MAX_HISTORY, maxlen=MAX_HISTORY)
        self.live_errors     = deque([0]*MAX_HISTORY, maxlen=MAX_HISTORY)
        self.current_baud = None
        self.status = "idle"       # idle | testing | done | error
        self.log = []

    def reset(self):
        self.results = {}
        self.live_throughput = deque([0]*MAX_HISTORY, maxlen=MAX_HISTORY)
        self.live_errors     = deque([0]*MAX_HISTORY, maxlen=MAX_HISTORY)
        self.log = []
        self.status = "idle"


# ── Main App ───────────────────────────────────────────────────────────────────
class SerialLoopbackPro(ctk.CTk):
    def __init__(self):
        super().__init__()
        self.title("Serial Loopback Test PRO")
        self.geometry("1200x800")
        self.minsize(900, 650)
        self.protocol("WM_DELETE_WINDOW", self._on_close)

        self.port_tests: dict[str, PortTest] = {}
        self.selected_port_name = None

        self._build_ui()
        self._refresh_ports()
        self._start_graph_loop()

    # ── UI Build ───────────────────────────────────────────────────────────────
    def _build_ui(self):
        # Top bar
        topbar = ctk.CTkFrame(self, height=56, corner_radius=0, fg_color="#1a1a2e")
        topbar.pack(fill="x")
        ctk.CTkLabel(topbar, text="⚡ Serial Loopback Test PRO",
                     font=ctk.CTkFont(size=18, weight="bold"),
                     text_color="#4fc3f7").pack(side="left", padx=16, pady=10)
        self.lbl_time = ctk.CTkLabel(topbar, text="", font=ctk.CTkFont(size=12),
                                     text_color="#90a4ae")
        self.lbl_time.pack(side="right", padx=16)
        self._tick_clock()

        # Main layout: sidebar + content
        main = ctk.CTkFrame(self, fg_color="transparent")
        main.pack(fill="both", expand=True, padx=8, pady=8)

        self._build_sidebar(main)
        self._build_content(main)

    def _build_sidebar(self, parent):
        sb = ctk.CTkFrame(parent, width=260, corner_radius=10)
        sb.pack(side="left", fill="y", padx=(0, 8))
        sb.pack_propagate(False)

        # Port section
        ctk.CTkLabel(sb, text="COM PORTS", font=ctk.CTkFont(size=11, weight="bold"),
                     text_color="#90a4ae").pack(anchor="w", padx=14, pady=(14, 2))

        port_frame = ctk.CTkFrame(sb, fg_color="transparent")
        port_frame.pack(fill="x", padx=10)
        self.btn_refresh = ctk.CTkButton(port_frame, text="↻ Refresh", width=80, height=28,
                                          command=self._refresh_ports)
        self.btn_refresh.pack(side="right")

        self.port_listbox_frame = ctk.CTkScrollableFrame(sb, height=160, corner_radius=6)
        self.port_listbox_frame.pack(fill="x", padx=10, pady=4)
        self.port_buttons: dict[str, ctk.CTkButton] = {}

        # Test config
        ctk.CTkLabel(sb, text="TEST CONFIG", font=ctk.CTkFont(size=11, weight="bold"),
                     text_color="#90a4ae").pack(anchor="w", padx=14, pady=(12, 2))

        ctk.CTkLabel(sb, text="Baud Rates:").pack(anchor="w", padx=14)
        self.baud_vars: dict[int, ctk.BooleanVar] = {}
        baud_frame = ctk.CTkScrollableFrame(sb, height=160, corner_radius=6)
        baud_frame.pack(fill="x", padx=10, pady=4)
        defaults_on = {9600, 19200, 38400, 57600, 115200}
        for b in BAUD_RATES:
            var = ctk.BooleanVar(value=(b in defaults_on))
            self.baud_vars[b] = var
            cb = ctk.CTkCheckBox(baud_frame, text=f"{b:,}", variable=var,
                                  font=ctk.CTkFont(size=12))
            cb.pack(anchor="w", pady=1)

        ctk.CTkLabel(sb, text="Test Pattern:").pack(anchor="w", padx=14, pady=(8,0))
        self.pattern_var = ctk.StringVar(value="Random")
        self.pattern_menu = ctk.CTkOptionMenu(
            sb, values=["Random", "0xFF fill", "Incrementing", "Custom string"],
            variable=self.pattern_var, command=self._on_pattern_change)
        self.pattern_menu.pack(fill="x", padx=10, pady=4)

        self.custom_entry = ctk.CTkEntry(sb, placeholder_text="Custom text...", state="disabled")
        self.custom_entry.pack(fill="x", padx=10, pady=(0,4))

        ctk.CTkLabel(sb, text="Bytes per baud:").pack(anchor="w", padx=14)
        self.bytes_slider = ctk.CTkSlider(sb, from_=64, to=4096, number_of_steps=32,
                                           command=self._on_bytes_slider)
        self.bytes_slider.set(512)
        self.bytes_slider.pack(fill="x", padx=10)
        self.bytes_label = ctk.CTkLabel(sb, text="512 bytes")
        self.bytes_label.pack(anchor="w", padx=14)

        # Action buttons
        ctk.CTkFrame(sb, height=1, fg_color="#333").pack(fill="x", padx=10, pady=12)

        self.btn_start = ctk.CTkButton(sb, text="▶  START TEST", height=40,
                                        font=ctk.CTkFont(size=14, weight="bold"),
                                        fg_color="#1565c0", hover_color="#1976d2",
                                        command=self._start_test)
        self.btn_start.pack(fill="x", padx=10, pady=4)

        self.btn_stop = ctk.CTkButton(sb, text="■  STOP", height=36,
                                       fg_color="#b71c1c", hover_color="#c62828",
                                       state="disabled", command=self._stop_test)
        self.btn_stop.pack(fill="x", padx=10, pady=4)

        self.btn_export = ctk.CTkButton(sb, text="⬇  Export CSV", height=32,
                                         fg_color="#2e7d32", hover_color="#388e3c",
                                         command=self._export_csv)
        self.btn_export.pack(fill="x", padx=10, pady=4)

        self.btn_clear = ctk.CTkButton(sb, text="🗑  Clear Results", height=32,
                                        fg_color="#37474f", hover_color="#455a64",
                                        command=self._clear_results)
        self.btn_clear.pack(fill="x", padx=10, pady=(4,14))

    def _build_content(self, parent):
        content = ctk.CTkFrame(parent, corner_radius=10)
        content.pack(side="left", fill="both", expand=True)

        # Tab view
        self.tabs = ctk.CTkTabview(content, corner_radius=8)
        self.tabs.pack(fill="both", expand=True, padx=8, pady=8)

        self.tabs.add("📊 Results")
        self.tabs.add("📈 Live Graph")
        self.tabs.add("📝 Log")

        self._build_results_tab(self.tabs.tab("📊 Results"))
        self._build_graph_tab(self.tabs.tab("📈 Live Graph"))
        self._build_log_tab(self.tabs.tab("📝 Log"))

    def _build_results_tab(self, parent):
        # Status bar
        status_row = ctk.CTkFrame(parent, fg_color="transparent")
        status_row.pack(fill="x", pady=(0,6))
        self.lbl_port_status = ctk.CTkLabel(status_row, text="No port selected",
                                             font=ctk.CTkFont(size=13), text_color="#90a4ae")
        self.lbl_port_status.pack(side="left")
        self.progress_bar = ctk.CTkProgressBar(status_row, width=160)
        self.progress_bar.set(0)
        self.progress_bar.pack(side="right")
        self.lbl_progress = ctk.CTkLabel(status_row, text="0%", width=36,
                                          font=ctk.CTkFont(size=11))
        self.lbl_progress.pack(side="right", padx=4)

        # Summary cards
        cards = ctk.CTkFrame(parent, fg_color="transparent")
        cards.pack(fill="x", pady=(0,8))
        self.card_passed = self._make_card(cards, "PASSED", "—", "#2e7d32")
        self.card_failed = self._make_card(cards, "FAILED", "—", "#b71c1c")
        self.card_quality = self._make_card(cards, "AVG QUALITY", "—", "#1565c0")
        self.card_latency = self._make_card(cards, "AVG LATENCY", "—", "#6a1b9a")
        for c in [self.card_passed, self.card_failed, self.card_quality, self.card_latency]:
            c.pack(side="left", fill="x", expand=True, padx=4)

        # Results table header
        header = ctk.CTkFrame(parent, fg_color="#263238", corner_radius=6, height=32)
        header.pack(fill="x", pady=(0,2))
        header.pack_propagate(False)
        for col, w in [("Baud Rate",120),("Sent",80),("Received",80),
                        ("Errors",80),("Quality",90),("Latency",90),("Status",80)]:
            ctk.CTkLabel(header, text=col, width=w,
                         font=ctk.CTkFont(size=11, weight="bold"),
                         text_color="#b0bec5").pack(side="left", padx=4)

        # Scrollable results rows
        self.results_scroll = ctk.CTkScrollableFrame(parent, corner_radius=6)
        self.results_scroll.pack(fill="both", expand=True)
        self.result_rows: dict[int, dict] = {}  # baud -> row widgets

    def _make_card(self, parent, title, value, color):
        f = ctk.CTkFrame(parent, corner_radius=8, fg_color="#1e2a38")
        ctk.CTkLabel(f, text=title, font=ctk.CTkFont(size=10, weight="bold"),
                     text_color="#78909c").pack(pady=(8,0))
        lbl = ctk.CTkLabel(f, text=value, font=ctk.CTkFont(size=22, weight="bold"),
                           text_color=color)
        lbl.pack(pady=(0,8))
        f._value_label = lbl
        return f

    def _build_graph_tab(self, parent):
        fig = Figure(figsize=(8, 4), facecolor="#1a1a2e")
        self._fig = fig

        ax1 = fig.add_subplot(211)
        ax2 = fig.add_subplot(212)
        self._ax_tput = ax1
        self._ax_err  = ax2

        for ax, ylabel, color in [
            (ax1, "Throughput (B/s)", "#4fc3f7"),
            (ax2, "Errors / sec",     "#ef5350"),
        ]:
            ax.set_facecolor("#0d1117")
            ax.set_ylabel(ylabel, color=color, fontsize=9)
            ax.tick_params(colors="#78909c", labelsize=8)
            for spine in ax.spines.values():
                spine.set_color("#333")

        fig.tight_layout(pad=2)

        canvas = FigureCanvasTkAgg(fig, parent)
        canvas.get_tk_widget().pack(fill="both", expand=True)
        self._canvas = canvas

        self._line_tput, = ax1.plot([], [], color="#4fc3f7", linewidth=1.5)
        self._line_err,  = ax2.plot([], [], color="#ef5350", linewidth=1.5)

    def _build_log_tab(self, parent):
        self.log_text = ctk.CTkTextbox(parent, font=ctk.CTkFont(family="Consolas", size=11),
                                        fg_color="#0d1117", text_color="#b0bec5",
                                        corner_radius=6)
        self.log_text.pack(fill="both", expand=True)
        self.log_text.configure(state="disabled")

    # ── Port list ─────────────────────────────────────────────────────────────
    def _refresh_ports(self):
        for w in self.port_listbox_frame.winfo_children():
            w.destroy()
        self.port_buttons.clear()

        ports = sorted(serial.tools.list_ports.comports(), key=lambda p: p.device)
        if not ports:
            ctk.CTkLabel(self.port_listbox_frame, text="No ports found",
                         text_color="#546e7a").pack(pady=8)
            return

        for p in ports:
            name = p.device
            if name not in self.port_tests:
                self.port_tests[name] = PortTest(name)
            desc = p.description[:28] if p.description else name
            btn = ctk.CTkButton(
                self.port_listbox_frame,
                text=f"{name}\n{desc}",
                height=44, anchor="w",
                fg_color="#1e2a38", hover_color="#263238",
                font=ctk.CTkFont(size=11),
                command=lambda n=name: self._select_port(n)
            )
            btn.pack(fill="x", pady=2)
            self.port_buttons[name] = btn

        if not self.selected_port_name and ports:
            self._select_port(ports[0].device)

    def _select_port(self, name):
        self.selected_port_name = name
        for n, b in self.port_buttons.items():
            b.configure(fg_color="#1565c0" if n == name else "#1e2a38")
        self._refresh_results_display()
        self.lbl_port_status.configure(text=f"Port: {name}")

    # ── Test logic ────────────────────────────────────────────────────────────
    def _get_test_bytes(self, n):
        pat = self.pattern_var.get()
        if pat == "0xFF fill":
            return bytes([0xFF] * n)
        elif pat == "Incrementing":
            return bytes([i % 256 for i in range(n)])
        elif pat == "Custom string":
            s = self.custom_entry.get() or "TEST"
            raw = (s * ((n // len(s)) + 1)).encode()[:n]
            return raw
        else:  # Random
            return bytes(random.getrandbits(8) for _ in range(n))

    def _start_test(self):
        if not self.selected_port_name:
            messagebox.showwarning("No Port", "Please select a COM port first.")
            return
        bauds = [b for b, v in self.baud_vars.items() if v.get()]
        if not bauds:
            messagebox.showwarning("No Baud", "Select at least one baud rate.")
            return

        pt = self.port_tests[self.selected_port_name]
        if pt.running:
            return
        pt.reset()
        pt.running = True
        self.btn_start.configure(state="disabled")
        self.btn_stop.configure(state="normal")

        n_bytes = int(self.bytes_slider.get())
        pt.thread = threading.Thread(
            target=self._run_test, args=(pt, bauds, n_bytes), daemon=True)
        pt.thread.start()

    def _run_test(self, pt: PortTest, bauds: list, n_bytes: int):
        total = len(bauds)
        for idx, baud in enumerate(sorted(bauds)):
            if not pt.running:
                break
            pt.current_baud = baud
            pt.status = "testing"
            self._log(f"[{pt.port}] Testing {baud:,} baud...")
            self._update_progress(idx / total)

            sent = errors = 0
            latencies = []

            try:
                with serial.Serial(pt.port, baud, timeout=2) as ser:
                    ser.reset_input_buffer()
                    data_out = self._get_test_bytes(n_bytes)
                    chunk = 32

                    for i in range(0, len(data_out), chunk):
                        if not pt.running:
                            break
                        block = data_out[i:i+chunk]
                        t0 = time.perf_counter()
                        ser.write(block)
                        received = ser.read(len(block))
                        t1 = time.perf_counter()

                        sent += len(block)
                        latency_ms = (t1 - t0) * 1000
                        latencies.append(latency_ms)

                        if len(received) != len(block):
                            errors += len(block) - len(received)
                        else:
                            errors += sum(a != b for a, b in zip(block, received))

                        # live graph update
                        tput = sent / max(t1 - t0, 0.001)
                        pt.live_throughput.append(tput)
                        pt.live_errors.append(errors)

            except serial.SerialException as e:
                self._log(f"[{pt.port}] ERROR at {baud}: {e}")
                pt.results[baud] = {"sent": 0, "errors": -1, "latency_ms": 0}
                continue

            quality = max(0, (1 - errors / max(sent, 1)) * 100)
            avg_lat = sum(latencies) / len(latencies) if latencies else 0
            pt.results[baud] = {"sent": sent, "errors": errors,
                                  "latency_ms": avg_lat, "quality": quality}
            self._log(f"[{pt.port}] {baud:,} → Q:{quality:.1f}%  Err:{errors}  Lat:{avg_lat:.1f}ms")
            self.after(0, self._refresh_results_display)

        pt.running = False
        pt.status = "done"
        pt.current_baud = None
        self.after(0, self._test_finished)

    def _stop_test(self):
        if self.selected_port_name:
            pt = self.port_tests[self.selected_port_name]
            pt.running = False
        self._test_finished()

    def _test_finished(self):
        self.btn_start.configure(state="normal")
        self.btn_stop.configure(state="disabled")
        self._update_progress(1.0)
        self._refresh_results_display()
        self._update_summary_cards()
        self._log("── Test complete ──")

    # ── Results display ───────────────────────────────────────────────────────
    def _refresh_results_display(self):
        if not self.selected_port_name:
            return
        pt = self.port_tests[self.selected_port_name]

        # Clear old rows
        for w in self.results_scroll.winfo_children():
            w.destroy()
        self.result_rows.clear()

        bauds = [b for b, v in self.baud_vars.items() if v.get()]
        for baud in sorted(bauds):
            row = ctk.CTkFrame(self.results_scroll, corner_radius=4,
                                fg_color="#1e2a38" if baud % 2 == 0 else "#1a2433",
                                height=34)
            row.pack(fill="x", pady=1)
            row.pack_propagate(False)

            res = pt.results.get(baud)
            is_current = (pt.current_baud == baud and pt.running)

            if res is None:
                vals = [f"{baud:,}", "—", "—", "—", "—", "—",
                        "⏳ Testing..." if is_current else "Pending"]
                colors = ["#cfd8dc","#90a4ae","#90a4ae","#90a4ae","#90a4ae","#90a4ae",
                          "#ffa726" if is_current else "#546e7a"]
            elif res.get("errors") == -1:
                vals = [f"{baud:,}", "—","—","—","—","—","❌ Error"]
                colors = ["#cfd8dc"]+["#90a4ae"]*5+["#ef5350"]
            else:
                q = res["quality"]
                qcol = "#66bb6a" if q >= 95 else ("#ffa726" if q >= 70 else "#ef5350")
                status = "✅ PASS" if q >= 90 else "⚠ WARN" if q >= 70 else "❌ FAIL"
                scol   = "#66bb6a" if q >= 90 else ("#ffa726" if q >= 70 else "#ef5350")
                vals   = [f"{baud:,}", str(res['sent']), str(res['sent']-res['errors']),
                          str(res['errors']), f"{q:.1f}%", f"{res['latency_ms']:.1f}ms", status]
                colors = ["#cfd8dc","#90a4ae","#90a4ae","#ef5350" if res['errors'] else "#90a4ae",
                          qcol,"#ce93d8",scol]

            widths = [120, 80, 80, 80, 90, 90, 80]
            for val, col, w in zip(vals, colors, widths):
                ctk.CTkLabel(row, text=val, width=w, text_color=col,
                             font=ctk.CTkFont(size=11)).pack(side="left", padx=4)

    def _update_summary_cards(self):
        if not self.selected_port_name:
            return
        pt = self.port_tests[self.selected_port_name]
        if not pt.results:
            return

        valid = {b: r for b, r in pt.results.items() if r.get("errors", -1) != -1}
        passed = sum(1 for r in valid.values() if r["quality"] >= 90)
        failed = len(valid) - passed
        avg_q  = sum(r["quality"] for r in valid.values()) / len(valid) if valid else 0
        avg_l  = sum(r["latency_ms"] for r in valid.values()) / len(valid) if valid else 0

        self.card_passed._value_label.configure(text=str(passed))
        self.card_failed._value_label.configure(text=str(failed))
        self.card_quality._value_label.configure(text=f"{avg_q:.1f}%")
        self.card_latency._value_label.configure(text=f"{avg_l:.1f}ms")

    def _update_progress(self, frac):
        self.progress_bar.set(frac)
        self.lbl_progress.configure(text=f"{int(frac*100)}%")

    # ── Live graph ────────────────────────────────────────────────────────────
    def _start_graph_loop(self):
        self._update_graph()

    def _update_graph(self):
        if self.selected_port_name and self.selected_port_name in self.port_tests:
            pt = self.port_tests[self.selected_port_name]
            x = list(range(MAX_HISTORY))
            self._line_tput.set_data(x, list(pt.live_throughput))
            self._line_err.set_data(x, list(pt.live_errors))
            self._ax_tput.relim(); self._ax_tput.autoscale_view()
            self._ax_err.relim();  self._ax_err.autoscale_view()
            try:
                self._canvas.draw_idle()
            except Exception:
                pass
        self.after(500, self._update_graph)

    # ── Log ───────────────────────────────────────────────────────────────────
    def _log(self, msg):
        ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
        line = f"[{ts}] {msg}\n"
        if self.selected_port_name:
            self.port_tests[self.selected_port_name].log.append(line)
        self.after(0, lambda: self._append_log(line))

    def _append_log(self, line):
        self.log_text.configure(state="normal")
        self.log_text.insert("end", line)
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    # ── Export ────────────────────────────────────────────────────────────────
    def _export_csv(self):
        if not self.selected_port_name:
            messagebox.showinfo("No port", "Select a port first.")
            return
        pt = self.port_tests[self.selected_port_name]
        if not pt.results:
            messagebox.showinfo("No data", "Run a test first.")
            return

        path = filedialog.asksaveasfilename(
            defaultextension=".csv",
            filetypes=[("CSV", "*.csv")],
            initialfile=f"loopback_{pt.port}_{datetime.now():%Y%m%d_%H%M%S}.csv"
        )
        if not path:
            return

        with open(path, "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["Port", "Baud Rate", "Bytes Sent", "Errors",
                        "Quality %", "Avg Latency ms", "Status"])
            for baud, res in sorted(pt.results.items()):
                if res.get("errors") == -1:
                    w.writerow([pt.port, baud, 0, "ERROR", 0, 0, "ERROR"])
                else:
                    status = "PASS" if res["quality"] >= 90 else "WARN" if res["quality"] >= 70 else "FAIL"
                    w.writerow([pt.port, baud, res["sent"], res["errors"],
                                f"{res['quality']:.2f}", f"{res['latency_ms']:.2f}", status])
        messagebox.showinfo("Exported", f"Saved to:\n{path}")

    # ── Misc ──────────────────────────────────────────────────────────────────
    def _on_pattern_change(self, val):
        self.custom_entry.configure(state="normal" if val == "Custom string" else "disabled")

    def _on_bytes_slider(self, val):
        self.bytes_label.configure(text=f"{int(val)} bytes")

    def _clear_results(self):
        if self.selected_port_name:
            self.port_tests[self.selected_port_name].reset()
            for w in self.results_scroll.winfo_children():
                w.destroy()
            for card in [self.card_passed, self.card_failed, self.card_quality, self.card_latency]:
                card._value_label.configure(text="—")
            self.log_text.configure(state="normal")
            self.log_text.delete("1.0", "end")
            self.log_text.configure(state="disabled")
            self._update_progress(0)

    def _tick_clock(self):
        self.lbl_time.configure(text=datetime.now().strftime("%Y-%m-%d  %H:%M:%S"))
        self.after(1000, self._tick_clock)

    def _on_close(self):
        for pt in self.port_tests.values():
            pt.running = False
        self.destroy()


if __name__ == "__main__":
    app = SerialLoopbackPro()
    app.mainloop()
