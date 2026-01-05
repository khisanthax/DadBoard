import json
import os
import subprocess
import sys
from datetime import datetime, timezone
import tkinter as tk
from tkinter import ttk, messagebox

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(BASE_DIR, os.pardir))
SHARED_DIR = os.path.join(REPO_ROOT, 'shared')


def load_json(path):
    with open(path, 'r', encoding='utf-8') as f:
        return json.load(f)


def parse_iso(ts):
    if not ts:
        return None
    try:
        if ts.endswith('Z'):
            ts = ts[:-1] + '+00:00'
        return datetime.fromisoformat(ts)
    except Exception:
        return None


def format_age(seconds):
    if seconds is None:
        return 'UNKNOWN'
    if seconds < 60:
        return f'{int(seconds)}s'
    if seconds < 3600:
        return f'{int(seconds // 60)}m{int(seconds % 60)}s'
    return f'{int(seconds // 3600)}h{int((seconds % 3600) // 60)}m'


class DadBoardApp(tk.Tk):
    def __init__(self, config, games):
        super().__init__()
        self.title('DadBoard')
        self.geometry('1100x500')
        self.config_data = config
        self.games = games
        self.status_cache = {}
        self.poll_ms = int(config.get('pollIntervalSec', 2) * 1000)

        self._build_ui()
        self.refresh_statuses()
        self.after(self.poll_ms, self._auto_refresh)

    def _build_ui(self):
        top = ttk.Frame(self)
        top.pack(fill='x', padx=10, pady=8)

        self.ready_var = tk.StringVar(value='All PCs Ready: NO')
        ready_label = ttk.Label(top, textvariable=self.ready_var, font=('Segoe UI', 12, 'bold'))
        ready_label.pack(side='left')

        self.status_var = tk.StringVar(value='')
        status_label = ttk.Label(top, textvariable=self.status_var)
        status_label.pack(side='right')

        main = ttk.Frame(self)
        main.pack(fill='both', expand=True, padx=10, pady=8)

        left = ttk.Frame(main)
        left.pack(side='left', fill='y', padx=(0, 10))

        ttk.Label(left, text='Games').pack(anchor='w')
        self.game_list = tk.Listbox(left, height=12, exportselection=False)
        for game in self.games:
            self.game_list.insert(tk.END, game['name'])
        self.game_list.pack(fill='y')

        btn_frame = ttk.Frame(left)
        btn_frame.pack(fill='x', pady=(10, 0))
        ttk.Button(btn_frame, text='Launch on all', command=self.launch_on_all).pack(fill='x')
        ttk.Button(btn_frame, text='Refresh', command=self.refresh_statuses).pack(fill='x', pady=(6, 0))

        table = ttk.Frame(main)
        table.pack(side='left', fill='both', expand=True)

        headers = ['PC', 'Online', 'Steam', 'Game', 'LaunchResult', 'InviteResult', 'LastUpdate', 'Ready', '']
        for col, header in enumerate(headers):
            ttk.Label(table, text=header, font=('Segoe UI', 9, 'bold')).grid(row=0, column=col, sticky='w', padx=4, pady=2)

        self.rows = {}
        pcs = self.config_data.get('pcs', [])
        for row_idx, pc in enumerate(pcs, start=1):
            labels = []
            for col in range(len(headers) - 1):
                lbl = ttk.Label(table, text='-')
                lbl.grid(row=row_idx, column=col, sticky='w', padx=4, pady=2)
                labels.append(lbl)

            btn = ttk.Button(table, text='Accept Invite', command=lambda p=pc: self.accept_invite(p))
            btn.grid(row=row_idx, column=len(headers) - 1, sticky='w', padx=4, pady=2)

            self.rows[pc] = {
                'labels': labels,
                'button': btn,
            }

        for col in range(len(headers)):
            table.grid_columnconfigure(col, weight=1)

    def _status_path(self, pc):
        share = self.config_data.get('shareName', 'DadBoard$')
        status_file = self.config_data.get('statusFile', 'status.json')
        return rf'\\{pc}\{share}\{status_file}'

    def _read_status(self, pc):
        path = self._status_path(pc)
        try:
            with open(path, 'r', encoding='utf-8') as f:
                data = json.load(f)
        except Exception as exc:
            return {
                'pc': pc,
                'online': False,
                'error': str(exc),
            }

        last_update = parse_iso(data.get('lastUpdate'))
        if not last_update:
            try:
                last_update = datetime.fromtimestamp(os.path.getmtime(path), tz=timezone.utc)
            except Exception:
                last_update = None

        now = datetime.now(timezone.utc)
        age_sec = (now - last_update).total_seconds() if last_update else None
        stale_minutes = int(self.config_data.get('staleMinutes', 5))
        stale = age_sec is not None and age_sec > stale_minutes * 60

        steam_running = bool(data.get('steam', {}).get('running', False))
        game_running = bool(data.get('game', {}).get('running', False))
        game_name = data.get('game', {}).get('name')
        launch_result = data.get('game', {}).get('launchResult')
        invite_result = data.get('invite', {}).get('result')

        ready = steam_running and game_running and (not stale)

        return {
            'pc': pc,
            'online': True,
            'stale': stale,
            'age_sec': age_sec,
            'steam': steam_running,
            'game': game_running,
            'game_name': game_name,
            'launch_result': launch_result,
            'invite_result': invite_result,
            'ready': ready,
        }

    def refresh_statuses(self):
        pcs = self.config_data.get('pcs', [])
        all_ready = True
        for pc in pcs:
            info = self._read_status(pc)
            self.status_cache[pc] = info

            labels = self.rows[pc]['labels']
            if not info.get('online'):
                labels[0].config(text=pc)
                labels[1].config(text='OFFLINE')
                labels[2].config(text='-')
                labels[3].config(text='-')
                labels[4].config(text='-')
                labels[5].config(text='-')
                labels[6].config(text='-')
                labels[7].config(text='NO')
                all_ready = False
                continue

            steam = 'YES' if info.get('steam') else 'NO'
            game = 'YES' if info.get('game') else 'NO'
            game_name = info.get('game_name') or '-'
            launch_result = info.get('launch_result') or '-'
            invite_result = info.get('invite_result') or '-'
            age_text = format_age(info.get('age_sec'))
            if info.get('stale'):
                age_text = f'STALE ({age_text})'
            ready_text = 'YES' if info.get('ready') else 'NO'

            labels[0].config(text=pc)
            labels[1].config(text='ONLINE')
            labels[2].config(text=steam)
            labels[3].config(text=f'{game} ({game_name})')
            labels[4].config(text=launch_result)
            labels[5].config(text=invite_result)
            labels[6].config(text=age_text)
            labels[7].config(text=ready_text)

            if not info.get('ready'):
                all_ready = False

        self.ready_var.set('All PCs Ready: YES' if all_ready else 'All PCs Ready: NO')

    def _auto_refresh(self):
        try:
            self.refresh_statuses()
        finally:
            self.after(self.poll_ms, self._auto_refresh)

    def _selected_game(self):
        if not self.game_list.curselection():
            return None
        idx = self.game_list.curselection()[0]
        return self.games[idx]

    def _run_schtasks(self, pc, task_name):
        try:
            result = subprocess.run(
                ['schtasks', '/Run', '/S', pc, '/TN', task_name],
                capture_output=True,
                text=True,
                check=False
            )
        except Exception as exc:
            return False, str(exc)

        if result.returncode != 0:
            msg = result.stderr.strip() or result.stdout.strip()
            return False, msg or f'Command failed with code {result.returncode}'
        return True, result.stdout.strip()

    def launch_on_all(self):
        game = self._selected_game()
        if not game:
            messagebox.showwarning('DadBoard', 'Select a game first.')
            return

        appid = game['appid']
        task_name = f'DadBoard_LaunchGame_{appid}'
        pcs = self.config_data.get('pcs', [])
        failures = []

        for pc in pcs:
            ok, msg = self._run_schtasks(pc, task_name)
            if not ok:
                failures.append(f'{pc}: {msg}')

        if failures:
            self.status_var.set('Launch errors: ' + '; '.join(failures))
        else:
            self.status_var.set('Launch triggered on all PCs')

    def accept_invite(self, pc):
        ok, msg = self._run_schtasks(pc, 'DadBoard_AcceptInvite')
        if not ok:
            self.status_var.set(f'Accept invite error on {pc}: {msg}')
        else:
            self.status_var.set(f'Accept invite triggered on {pc}')


def main():
    config_path = os.path.join(SHARED_DIR, 'config.json')
    games_path = os.path.join(BASE_DIR, 'games.json')

    try:
        config = load_json(config_path)
        games = load_json(games_path)
    except Exception as exc:
        print(f'Failed to load config: {exc}')
        sys.exit(1)

    app = DadBoardApp(config, games)
    app.mainloop()


if __name__ == '__main__':
    main()
