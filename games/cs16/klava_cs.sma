/*
 * Мост CS 1.6 <-> Клава (KlavaDesk).
 *
 * Клава играет в CS отдельным игроком-ботом с ником Klava (PODBot: реакция и прицел как у живого), а плагин
 * связывает игру с ней по TCP 127.0.0.1:27099: события — ей (её фраги и смерти, раунды, бомба, ранения), её
 * команды — в игру (зайти, выйти, боты-противники, сказать в чат, показать игру её глазами, счёт).
 * Сервер — любая игра на этой машине (своя «Новая игра» или hlds); плагин сам переподключается раз в 2 с.
 *
 * Строки Клаве:   hello <карта> | joined <команда> | left | kill <роль> <hs> <оружие> <убийца-человек> <жертва-человек>|<убийца>|<жертва>
 *                 round_start | score <T> <CT> | bomb planted|defused|exploded | hurt <hp> | status ...
 * Строки от неё:  join [t|ct|auto] [скилл] | leave | bots <сколько> [t|ct|enemy] [скилл] | say <текст> | watch | status | srv <команда>
 */
#include <amxmodx>
#include <amxmisc>
#include <sockets>
#include <fakemeta>
#include <cstrike>

#define PORT 27099

new g_sock
new g_klava
new g_buf[1024]
new g_bufLen
new g_scoreT
new g_scoreCT
new Float:g_lastHurt

public plugin_init()
{
	register_plugin("Klava Bridge", "1.0", "KlavaDesk")
	register_event("DeathMsg", "ev_death", "a")
	register_event("TeamScore", "ev_teamscore", "a")
	register_logevent("ev_round_start", 2, "1=Round_Start")
	register_event("SendAudio", "ev_planted", "a", "2&%!MRAD_BOMBPL")
	register_event("SendAudio", "ev_defused", "a", "2&%!MRAD_BOMBDEF")
	register_logevent("ev_exploded", 6, "3=Target_Bombed")
	register_event("Damage", "ev_damage", "b", "2!0")
	set_task(0.1, "task_poll", 0, "", 0, "b")
	set_task(2.0, "task_connect", 0, "", 0, "b")
}

public plugin_end()
{
	if (g_sock > 0)
	{
		socket_close(g_sock)
		g_sock = 0
	}
}

send_line(const fmt[], any:...)
{
	if (g_sock <= 0)
		return
	new msg[300]
	vformat(msg, 296, fmt, 2)
	add(msg, 299, "^n")
	socket_send(g_sock, msg, strlen(msg))
}

public task_connect()
{
	if (g_sock > 0)
		return
	new err = 0
	new s = socket_open("127.0.0.1", PORT, SOCKET_TCP, err)
	if (s <= 0 || err != 0)
		return
	g_sock = s
	new map[32]
	get_mapname(map, 31)
	send_line("hello %s", map)
	find_klava()
	if (g_klava)
		send_line("joined %s", team_name(g_klava))
}

public task_poll()
{
	if (g_sock <= 0)
		return
	while (g_sock > 0 && socket_change(g_sock, 1))
	{
		new chunk[512]
		new n = socket_recv(g_sock, chunk, 511)
		if (n <= 0)
		{
			socket_close(g_sock)
			g_sock = 0
			return
		}
		for (new i = 0; i < n; i++)
		{
			new c = chunk[i]
			if (c == '^n')
			{
				g_buf[g_bufLen] = 0
				if (g_bufLen > 0)
					handle(g_buf)
				g_bufLen = 0
			}
			else if (c != '^r' && g_bufLen < 1022)
				g_buf[g_bufLen++] = c
		}
	}
}

find_klava()
{
	g_klava = 0
	new name[32]
	new maxp = get_maxplayers()
	for (new i = 1; i <= maxp; i++)
	{
		if (!is_user_connected(i) || !is_user_bot(i))
			continue
		get_user_name(i, name, 31)
		if (containi(name, "Klava") != -1)
		{
			g_klava = i
			return
		}
	}
}

/* первый живой человек на сервере — хозяин игры */
host_player()
{
	new maxp = get_maxplayers()
	for (new i = 1; i <= maxp; i++)
		if (is_user_connected(i) && !is_user_bot(i) && !is_user_hltv(i))
			return i
	return 0
}

team_name(id)
{
	new t[8]
	switch (cs_get_user_team(id))
	{
		case CS_TEAM_T: copy(t, 7, "T")
		case CS_TEAM_CT: copy(t, 7, "CT")
		case CS_TEAM_SPECTATOR: copy(t, 7, "SPEC")
		default: copy(t, 7, "NONE")
	}
	return t
}

/* номер команды для PODBot: 1 — T, 2 — CT; want = t | ct | auto (к хозяину) | enemy (против хозяина) */
team_for(const want[])
{
	if (equali(want, "t"))
		return 1
	if (equali(want, "ct"))
		return 2
	new host = host_player()
	new CsTeams:ht = host ? cs_get_user_team(host) : CS_TEAM_UNASSIGNED
	if (equali(want, "enemy"))
		return ht == CS_TEAM_T ? 2 : 1
	if (ht == CS_TEAM_T)
		return 1
	if (ht == CS_TEAM_CT)
		return 2
	return 5
}

handle(const line[])
{
	new cmd[32], arg[256]
	strbreak(line, cmd, 31, arg, 255)
	trim(arg)
	if (equal(cmd, "say"))
		klava_say(arg)
	else if (equal(cmd, "join"))
		klava_join(arg)
	else if (equal(cmd, "leave"))
	{
		find_klava()
		if (g_klava)
			server_cmd("kick #%d", get_user_userid(g_klava))
	}
	else if (equal(cmd, "bots"))
		add_bots(arg)
	else if (equal(cmd, "watch"))
		watch_klava()
	else if (equal(cmd, "status"))
		send_status()
	else if (equal(cmd, "srv"))
		safe_server_cmd(arg)
}

klava_join(const arg[])
{
	find_klava()
	if (g_klava)
	{
		send_line("joined %s", team_name(g_klava))
		return
	}
	new want[8], rest[16]
	strbreak(arg, want, 7, rest, 15)
	new skill = str_to_num(rest)
	if (skill <= 0 || skill > 100)
		skill = 95
	if (!want[0])
		copy(want, 7, "auto")
	/* pb add <скилл> <характер: 2 — боевой> <команда> <модель: 5 — любая> <имя> */
	server_cmd("pb add %d 2 %d 5 Klava", skill, team_for(want))
}

add_bots(const arg[])
{
	new num[8], rest[32], side[8], sk[8]
	strbreak(arg, num, 7, rest, 31)
	strbreak(rest, side, 7, sk, 7)
	new count = clamp(str_to_num(num), 1, 10)
	new skill = str_to_num(sk)
	if (skill <= 0 || skill > 100)
		skill = 80
	if (!side[0])
		copy(side, 7, "enemy")
	new team = team_for(side)
	for (new i = 0; i < count; i++)
		server_cmd("pb add %d 1 %d 5", skill, team)
}

klava_say(const text[])
{
	if (!text[0])
		return
	find_klava()
	if (g_klava && is_user_connected(g_klava))
		engclient_cmd(g_klava, "say", text)
	else
		client_print(0, print_chat, "[Klava] %s", text)
}

/* показать игру глазами Клавы: хозяин в наблюдателях или мёртв — камера от первого лица на неё */
watch_klava()
{
	find_klava()
	new host = host_player()
	if (!g_klava || !host)
	{
		send_line("watch no")
		return
	}
	if (is_user_alive(host))
	{
		send_line("watch alive")
		return
	}
	set_pev(host, pev_iuser1, 4)
	set_pev(host, pev_iuser2, g_klava)
	send_line("watch ok")
}

send_status()
{
	find_klava()
	if (!g_klava)
	{
		send_line("status score %d %d klava none", g_scoreT, g_scoreCT)
		return
	}
	send_line("status score %d %d klava %s %d %d %d %d", g_scoreT, g_scoreCT, team_name(g_klava),
		get_user_frags(g_klava), cs_get_user_deaths(g_klava), get_user_health(g_klava), is_user_alive(g_klava))
}

/* только игровые настройки: карта, правила, боты, рестарт, кик */
safe_server_cmd(const arg[])
{
	if (equal(arg, "map ", 4) || equal(arg, "changelevel ", 12) || equal(arg, "mp_", 3) || equal(arg, "pb ", 3)
		|| equal(arg, "sv_restart", 10) || equal(arg, "kick ", 5) || equal(arg, "bot_", 4))
		server_cmd("%s", arg)
	else
		send_line("srv denied")
}

public client_putinserver(id)
{
	if (!is_user_bot(id))
		return
	new name[32]
	get_user_name(id, name, 31)
	if (containi(name, "Klava") != -1)
	{
		g_klava = id
		set_task(1.5, "task_joined", id)
	}
}

public task_joined(id)
{
	if (id == g_klava && is_user_connected(id))
		send_line("joined %s", team_name(id))
}

public client_disconnect(id)
{
	if (id == g_klava)
	{
		g_klava = 0
		send_line("left")
	}
}

public ev_death()
{
	new killer = read_data(1)
	new victim = read_data(2)
	new hs = read_data(3)
	new weapon[24], kn[32], vn[32], role[8]
	read_data(4, weapon, 23)
	if (killer > 0 && killer <= 32 && is_user_connected(killer))
		get_user_name(killer, kn, 31)
	else
		copy(kn, 31, "world")
	get_user_name(victim, vn, 31)
	if (g_klava && killer == g_klava && victim != g_klava)
		copy(role, 7, "killer")
	else if (g_klava && victim == g_klava)
		copy(role, 7, "victim")
	else
		copy(role, 7, "none")
	new kh = killer > 0 && killer <= 32 && is_user_connected(killer) && !is_user_bot(killer)
	new vh = is_user_connected(victim) && !is_user_bot(victim)
	send_line("kill %s %d %s %d %d|%s|%s", role, hs, weapon, kh, vh, kn, vn)
}

public ev_teamscore()
{
	new team[16]
	read_data(1, team, 15)
	new score = read_data(2)
	if (equal(team, "TERRORIST"))
		g_scoreT = score
	else if (equal(team, "CT"))
	{
		g_scoreCT = score
		send_line("score %d %d", g_scoreT, g_scoreCT)
	}
}

public ev_round_start()
{
	send_line("round_start")
}

public ev_planted()
{
	send_line("bomb planted")
}

public ev_defused()
{
	send_line("bomb defused")
}

public ev_exploded()
{
	send_line("bomb exploded")
}

public ev_damage(id)
{
	if (id != g_klava || !is_user_alive(id))
		return
	new Float:now = get_gametime()
	if (now - g_lastHurt < 3.0)
		return
	g_lastHurt = now
	send_line("hurt %d", get_user_health(id))
}
