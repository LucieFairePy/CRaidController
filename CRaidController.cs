using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using Oxide.Core;
using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("CRaidController", "Crazy (edit by Raul-Sorin Sorban)", "3.2.0")]
    [Description("System for managing raid schedules")]
    class CRaidController : RustPlugin
    {
        #region Fields        

        private static CRaidController _ins;
        private static string _sound = "assets/prefabs/locks/keypad/effects/lock.code.unlock.prefab";
        private static int _cupboardRadius = 20;
        private static int _timezone;
        private static int _uiCount = 6;
        private static float _radiusFireball = 5f;
        private static DateTime _lastWipe;
        private static string _permChange = "craidcontroller.change";
        private static string _permHide = "craidcontroller.hide";

        #endregion

        #region Server

        private void OnServerInitialized() => _lastWipe = SaveRestore.SaveCreatedTime.ToLocalTime();
        private void Loaded()
        {
            _ins = this;
            _timezone = int.Parse(cfg.config.timezone);

            LoadData();

            cmd.AddChatCommand(cfg.ui.command, this, nameof(PlayerConfCommand));
            permission.RegisterPermission(_permChange, this);
            permission.RegisterPermission(_permHide, this);

            if (!cfg.raid.ContainsKey("default"))
            {
                PrintError("Raid Schedules [default] don't exist !");
                return;
            }

            _controller = new Dictionary<ulong, RaidController>();

            foreach (var player in BasePlayer.activePlayerList)
                OnPlayerConnected(player);
        }
        private void Unload()
        {
            if (BasePlayer.activePlayerList.Count == 0)
                return;

            RaidController[] objects = UnityEngine.Object.FindObjectsOfType<RaidController>();
            if (objects != null)
            {
                foreach (var obj in objects)
                    UnityEngine.Object.Destroy(obj);
            }
        }
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player.IsReceivingSnapshot || player.IsSleeping())
            {
                timer.In(2, () => OnPlayerConnected(player));
                return;
            }
            if (!player.GetComponent<RaidController>())
                _controller[player.userID] = player.gameObject.AddComponent<RaidController>();
        }
        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (_controller.ContainsKey(player.userID))
                UnityEngine.Object.Destroy(_controller[player.userID]);
        }

        // Method updated by Death 06/04/2021
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!cfg.config.nullify || entity == null || info == null || info.Initiator == null)
            {
                return;
            }

            if (info.Initiator is FireBall || info.damageTypes.GetMajorityDamageType().ToString() == "heat")
            {
                var creator = info.Initiator.creatorEntity as BasePlayer;
                var controller = creator.GetComponent<RaidController>();
                if (creator != null && !TakeDamage(creator, entity, info, controller.CheckCanRaid(), controller.wipeCooldown) && !cfg.config.playerFire)
                {
                    Nullify(info);
                    return;
                }

                foreach (var x in fireBall.Where(x => x.Key.TotalSeconds >= 30))
                {
                    if (!IsNear(x.Value.radius, info.HitPositionWorld) || info.damageTypes.GetMajorityDamageType() != DamageType.Heat)
                    {
                        continue;
                    }

                    var p = BasePlayer.FindByID(x.Value.player);

                    if (p == null)
                    {
                        continue;
                    }

                    var pController = p.GetComponent<RaidController>();

                    if (pController == null)
                    {
                        continue;
                    }

                    if (!TakeDamage(p, entity, info, pController.CheckCanRaid(), pController.wipeCooldown))
                    {
                        info.damageTypes.ScaleAll(0f);
                    }
                }
            }

            if (!info.Initiator is BasePlayer)
            {
                return;
            }

            var player = info.InitiatorPlayer;

            if (player == null || player.IsNpc || player.net?.connection == null)
            {
                return;
            }

            var playerController = player.GetComponent<RaidController>();

            if (!TakeDamage(player, entity, info, playerController.CheckCanRaid(), playerController.wipeCooldown))
            {
                Nullify(info);
                CatchFireball(player, entity, info);
                Refund(player, info);

                if (cfg.config.tryDamageMessage == null)
                {
                    PrintError("The last update of the plugin requires you to delete your configuration file.");
                    return;
                }

                if (cfg.config.tryDamageMessage.enabled)
                {
                    if (pCd.ContainsKey($"damageCanRaid:{player.userID}"))
                    {
                        return;
                    }

                    int totalSeconds = (int)(ToTimeSpan(playerController.schedulesActual[1]) - playerController.time).TotalSeconds;
                    int minutes = (totalSeconds / 60) % 60;
                    int hours = (totalSeconds / (60 * 60));

                    if (minutes == 0 && hours == 0)
                    {
                        return;
                    }

                    if (playerController.schedulesNext[0] == string.Empty)
                    {
                        Message(player, "FinishTodayMessage");
                    }
                    else
                    {
                        Message(player, "AlertMessage", hours.ToString(), minutes.ToString());
                    }

                    if (cfg.config.tryDamageMessage.effect)
                    {
                        SendEffect(player, _sound);
                    }

                    pCd.Add($"damageCanRaid:{player.userID}", 5f);
                    RemoveCd("damageCanRaid", player.userID);
                }
            }
        }

        #endregion

        #region Component
        private class RaidController : FacepunchBehaviour
        {
            private BasePlayer player;
            private DayOfWeek dayOfWeek;
            private DateTime timeNow;
            public TimeSpan time;
            private List<SchedulesList> schedules;
            private string[] schedulesPrev;
            public string[] schedulesNext;
            public string[] schedulesActual;
            private string lang;
            private bool needUpdate;
            private bool allDayNoRaid;
            private bool allDayRaid;
            public bool wipeCooldown;
            private bool firstAlert;
            private void Awake()
            {
                player = GetComponent<BasePlayer>();

                Instance();
                InvokeRepeating(Refresh, 0f, cfg.ui.rate);
                InvokeRepeating(Create, 0f, cfg.ui.rate);

                if (cfg.config.alertMessage.enabled)
                    InvokeRepeating(AlertMessage, 0f, cfg.config.alertMessage.countdown * 60);
            }
            private void Instance()
            {
                lang = GetLanguage();
                time = DateTime.UtcNow.AddHours(_timezone).TimeOfDay;
                dayOfWeek = DateTime.UtcNow.AddHours(_timezone).DayOfWeek;
                timeNow = DateTime.UtcNow.AddHours(_timezone);
                schedules = GetDaySchedules();
                schedulesPrev = GetPrevSchedules();
                schedulesNext = GetNextSchedules();
                schedulesActual = GetActualSchedules();
                wipeCooldown = CheckWipeCooldown();
                allDayNoRaid = CheckAllDayNoRaid();
                allDayRaid = CheckAllDayRaid();
                needUpdate = false;

                if (CheckCanRaid())
                {
                    if (!cfg.config.timeMessage.enabled)
                        return;

                    Message(player, "TimeMessage", GetTime(schedulesActual[1]));

                    if (cfg.config.alertMessage.effect)
                        SendEffect(player, _sound);
                }
                else
                {
                    if (!cfg.config.timeFinishMessage.enabled)
                        return;

                    if (schedulesNext[0] == string.Empty)
                        Message(player, "FinishTodayMessage");
                    else
                        Message(player, "FinishMessage", GetTime(schedulesActual[1]));

                    if (cfg.config.alertMessage.effect)
                        SendEffect(player, _sound);
                }

                DestroyUI(true);

                if (player.IsAdmin && pConf.ContainsKey(player.userID))
                    if (pConf[player.userID].hide)
                        return;

                if (cfg.ui.allowPlayerhide && player.IPlayer.HasPermission(_permHide) && pConf.ContainsKey(player.userID))
                    if (pConf[player.userID].hide)
                        return;

                if (cfg.ui.hide)
                    return;

                CreateUI(true);
            }
            private void Refresh()
            {
                time = DateTime.UtcNow.AddHours(_timezone).TimeOfDay;

                CheckNeedUpdate();

                if (needUpdate)
                {
                    Instance();
                    return;
                }
            }
            private void AlertMessage()
            {
                if (!firstAlert)
                {
                    firstAlert = true;
                    return;
                }

                if (CheckCanRaid())
                    return;

                int totalSeconds = (int)(ToTimeSpan(schedulesActual[1]) - time).TotalSeconds;
                int minutes = (totalSeconds / 60) % 60;
                int hours = (totalSeconds / (60 * 60));

                if (minutes == 0 && hours == 0)
                    return;

                Message(player, "AlertMessage", hours.ToString(), minutes.ToString());

                if (cfg.config.alertMessage.effect)
                    SendEffect(player, _sound);
            }
            private string GetLanguage()
            {
                string default_lang = "en";

                if (_ins.lang.GetLanguage(player.IPlayer.Id) == "en-PT")
                    return default_lang;

                return _ins.lang.GetLanguage(player.IPlayer.Id);
            }
            private List<SchedulesList> GetDaySchedules()
            {
                List<SchedulesList> ret = new List<SchedulesList>();
                var schedules = cfg.raid.LastOrDefault(x => x.Key == player.userID.ToString() || CheckInGroup(player, x.Key));

                switch (dayOfWeek)
                {
                    case DayOfWeek.Monday:

                        foreach (var x in schedules.Value.monday.schedules)
                            ret.Add(new SchedulesList()
                            {
                                start = x.start,
                                end = x.end,
                                allday = schedules.Value.monday.allday,
                                noallday = schedules.Value.monday.noallday
                            });

                        break;
                    case DayOfWeek.Tuesday:

                        foreach (var x in schedules.Value.tuesday.schedules)
                            ret.Add(new SchedulesList()
                            {
                                start = x.start,
                                end = x.end,
                                allday = schedules.Value.tuesday.allday,
                                noallday = schedules.Value.tuesday.noallday
                            });

                        break;
                    case DayOfWeek.Wednesday:

                        foreach (var x in schedules.Value.wednesday.schedules)
                            ret.Add(new SchedulesList()
                            {
                                start = x.start,
                                end = x.end,
                                allday = schedules.Value.wednesday.allday,
                                noallday = schedules.Value.wednesday.noallday
                            });

                        break;
                    case DayOfWeek.Thursday:

                        foreach (var x in schedules.Value.thursday.schedules)
                            ret.Add(new SchedulesList()
                            {
                                start = x.start,
                                end = x.end,
                                allday = schedules.Value.thursday.allday,
                                noallday = schedules.Value.thursday.noallday
                            });

                        break;
                    case DayOfWeek.Friday:

                        foreach (var x in schedules.Value.friday.schedules)
                            ret.Add(new SchedulesList()
                            {
                                start = x.start,
                                end = x.end,
                                allday = schedules.Value.friday.allday,
                                noallday = schedules.Value.friday.noallday
                            });

                        break;
                    case DayOfWeek.Saturday:

                        foreach (var x in schedules.Value.saturday.schedules)
                            ret.Add(new SchedulesList()
                            {
                                start = x.start,
                                end = x.end,
                                allday = schedules.Value.saturday.allday,
                                noallday = schedules.Value.saturday.noallday
                            });

                        break;
                    case DayOfWeek.Sunday:

                        foreach (var x in schedules.Value.sunday.schedules)
                            ret.Add(new SchedulesList()
                            {
                                start = x.start,
                                end = x.end,
                                allday = schedules.Value.sunday.allday,
                                noallday = schedules.Value.sunday.noallday
                            });

                        break;
                }

                foreach (var x in ret.Where(x => x.end == "00:00"))
                    x.end = "23:59:59";

                return ret;
            }
            private string[] GetPrevSchedules()
            {
                string[] ret = new string[] { string.Empty, string.Empty };

                if (schedules.Exists(x => time > ToTimeSpan(x.end)))
                {
                    var _sch = schedules.FindLast(x => time > ToTimeSpan(x.end));
                    return new string[] { _sch.start, _sch.end };
                }

                return ret;
            }
            private string[] GetActualSchedules()
            {
                string[] ret = new string[] { string.Empty, string.Empty, string.Empty };

                if (!schedules.Any())
                    return ret;

                if (allDayNoRaid || allDayRaid)
                    return new string[] { "00:00", "23:59:59", string.Empty };

                else if (schedules.Exists(x => time > ToTimeSpan(x.start) && time < ToTimeSpan(x.end)))
                {
                    var _sch = schedules.FirstOrDefault(x => time > ToTimeSpan(x.start) && time < ToTimeSpan(x.end));
                    return new string[] { _sch.start, _sch.end, "true" };
                }
                else
                {
                    string retStart = string.Empty;
                    string retEnd = string.Empty;

                    if (schedulesPrev[1] == string.Empty)
                        retStart = "00:00";
                    else
                        retStart = schedulesPrev[1];

                    if (schedulesNext[0] == string.Empty)
                        retEnd = "23:59:59";
                    else
                        retEnd = schedulesNext[0];

                    return new string[] { retStart, retEnd, "false" };
                }

                return ret;
            }
            private string[] GetNextSchedules()
            {
                string[] ret = new string[] { string.Empty, string.Empty };

                if (schedules.Exists(x => time < ToTimeSpan(x.start)))
                {
                    var _sch = schedules.Find(x => time < ToTimeSpan(x.start));
                    return new string[] { _sch.start, _sch.end };
                }

                return ret;
            }
            private DateTime GetWipeCd()
            {
                var select = cfg.raid.LastOrDefault(x => x.Key == player.userID.ToString() || CheckInGroup(player, x.Key)).Value.wipeCooldown;
                DateTime parsedDate = DateTime.UtcNow.AddHours(_timezone);

                if (select.custom != null)
                {
                    string pattern = "dd-MM-yy HH:mm";

                    if (DateTime.TryParseExact(select.custom, pattern, null, DateTimeStyles.None, out parsedDate))
                        return parsedDate;
                    else
                        return parsedDate;
                }
                else if (select.days > 0)
                {
                    DateTime _lastWipeTime = _lastWipe.AddDays(select.days);
                    DateTime dateNew = new DateTime(_lastWipeTime.Year, _lastWipeTime.Month, _lastWipeTime.Day, 0, 0, 0);
                    return dateNew;
                }
                else
                    return parsedDate;
            }
            private string GetColor(string key) => color.FirstOrDefault(x => key == x.Key).Value;
            private string GetTitle()
            {
                if (wipeCooldown)
                    return _ins.GetLang("NoRaidCooldown", player.IPlayer.Id);
                else if (!CheckCanRaid())
                    return _ins.GetLang("NoRaidTime", player.IPlayer.Id);
                else
                    return _ins.GetLang("RaidTime", player.IPlayer.Id);
            }
            private string GetTime(string t)
            {
                if (lang == "en" && cfg.ui.time12)
                    return ToTime12(t);
                else
                    return ToTime24(t);
            }
            private void CheckNeedUpdate()
            {
                if (CheckLangChanged() || CheckDayChanged() || CheckSchedulesChanged())
                    needUpdate = true;

                if (needRefresh.Contains(player.userID))
                {
                    needRefresh.Remove(player.userID);
                    needUpdate = true;
                }
            }
            private bool CheckLangChanged()
            {
                var currentLang = GetLanguage();

                if (lang == currentLang)
                    return false;

                return true;
            }
            private bool CheckDayChanged()
            {
                DayOfWeek currentDay = DateTime.UtcNow.AddHours(_timezone).DayOfWeek;

                if (dayOfWeek == currentDay)
                    return false;

                return true;
            }
            private bool CheckSchedulesChanged()
            {
                if (time > ToTimeSpan(schedulesActual[1]))
                    return true;

                return false;
            }
            private bool CheckWipeCooldown()
            {
                var select = cfg.raid.LastOrDefault(x => x.Key == player.userID.ToString() || CheckInGroup(player, x.Key)).Value.wipeCooldown;

                if (select == null)
                    return false;

                if (select.enabled)
                {
                    if (select.custom != null)
                    {
                        string pattern = "dd-MM-yy HH:mm";
                        DateTime parsedDate;

                        if (DateTime.TryParseExact(select.custom, pattern, null, DateTimeStyles.None, out parsedDate))
                        {
                            if (DateTime.Now > parsedDate)
                                return false;

                            return true;
                        }
                        else
                            _ins.PrintError("Unable to convert '{0}' to a date and time. Pattern 'dd-MM HH:mm'", select.custom);
                    }

                    if (select.days == 0)
                        return false;

                    if (DateTime.Today > _lastWipe.AddDays(select.days))
                        return false;

                    return true;
                }

                return false;
            }
            private bool CheckAllDayRaid()
            {
                if (schedules[0].allday)
                    return true;

                return false;
            }
            private bool CheckAllDayNoRaid()
            {
                if (schedules[0].noallday)
                    return true;

                return false;
            }
            public bool CheckCanRaid()
            {
                if (allDayNoRaid)
                    return false;

                if (allDayRaid)
                    return true;

                if (schedulesActual[0] == string.Empty || schedulesActual[2] == "false")
                    return false;

                if (schedulesActual[2] == "true")
                    return true;

                TimeSpan start = ToTimeSpan(schedulesActual[0]);
                TimeSpan end = ToTimeSpan(schedulesActual[1]);

                if (time >= start && time < end)
                    return true;

                return false;
            }
            private void Create()
            {
                if (player.IsAdmin && pConf.ContainsKey(player.userID))
                    if (pConf[player.userID].hide)
                        return;

                if (cfg.ui.allowPlayerhide && player.IPlayer.HasPermission(_permHide) && pConf.ContainsKey(player.userID))
                    if (pConf[player.userID].hide)
                        return;

                if (!cfg.ui.hide)
                {
                    DestroyUI();
                    CreateUI();

                    if (cfg.ui.hideIfNotTime && !CheckCanRaid())
                        DestroyUI(true);

                    if (cfg.ui.hideIsTime && CheckCanRaid())
                        DestroyUI(true);
                }
            }
            private void CreateUI(bool all = false)
            {
                int uiChoice = 0;
                string start = schedulesActual[0];
                string end = schedulesActual[1];
                string color = string.Empty;
                string elapsedMinutes = "0";
                string totalMinutes = string.Empty;
                float progressCount = 0;
                DateTimeFormatInfo dateLang = (new CultureInfo(lang)).DateTimeFormat;

                if (wipeCooldown)
                {
                    elapsedMinutes = (timeNow - _lastWipe).TotalMinutes.ToString();
                    totalMinutes = (GetWipeCd() - _lastWipe).TotalMinutes.ToString();
                    progressCount = (float)float.Parse(elapsedMinutes) / float.Parse(totalMinutes);
                }
                else
                {
                    if (time < ToTimeSpan(end) && time >= ToTimeSpan(start))
                        elapsedMinutes = (time - ToTimeSpan(start)).TotalMinutes.ToString();

                    totalMinutes = (ToTimeSpan(end) - ToTimeSpan(start)).TotalMinutes.ToString();
                    progressCount = (float)float.Parse(elapsedMinutes) / float.Parse(totalMinutes);
                }

                if (!CheckCanRaid())
                    color = HexToRustFormat(GetColor("red"));
                else
                    color = HexToRustFormat(GetColor("green"));

                if (!cfg.ui.allowPlayerChange && !player.IPlayer.HasPermission(_permChange) || !pConf.ContainsKey(player.userID))
                    uiChoice = cfg.ui.uiChoice;
                else
                    if (pConf[player.userID].choose > 0 && pConf[player.userID].choose <= _uiCount)
                    uiChoice = pConf[player.userID].choose;
                else
                    uiChoice = cfg.ui.uiChoice;

                switch (uiChoice)
                {
                    #region UI #1

                    case 1:
                        if (all)
                        {
                            var container = UI.CreateElementContainer("craidcontroller.static.txt", cfg.ui.background, "0.78 0.89", "0.99 0.99");
                            UI.AddText(ref container, "craidcontroller.static.txt", GetTitle(), 14, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.MiddleLeft, "0.05 0.6", "1.0 1");
                            UI.AddText(ref container, "craidcontroller.static.txt", DateTime.UtcNow.AddHours(_timezone).ToString("dddd", dateLang).ToUpper(), 18, "robotocondensed-bold.ttf", "1 1 1 1", TextAnchor.MiddleLeft, "0.05 0", "1.0 1");
                            if (allDayRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidAllDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.MiddleLeft, "0.7 0.10", "1.0 1");
                            else if (allDayNoRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidNoDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.MiddleLeft, "0.7 0.10", "1.0 1");
                            else if (wipeCooldown)
                            {
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Until", player.IPlayer.Id, string.Empty).ToUpper(), 15, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.UpperCenter, "0.7 0", "1.0 0.9");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("dd-MM", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.MiddleCenter, "0.7 0.20", "1.0 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("HH:mm", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.LowerCenter, "0.7 0.30", "1.0 1");
                            }
                            else
                                UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTime(start)} / {GetTime(end)}", 18, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.MiddleCenter, "0.4 0.10", "1.0 1.1");
                            UI.AddImageColor(ref container, "craidcontroller.static.txt", HexToRustFormat(GetColor("blacklight")), "0 0", "1 0", "14 16", "0 22");
                            CuiHelper.AddUi(player, container);
                        }
                        else
                        {
                            var container = UI.CreateElementContainer("craidcontroller.progress.txt", "0 0 0 0", "0.78 0.89", "0.99 0.99");
                            UI.AddImageColor(ref container, "craidcontroller.progress.txt", color, "0 0", $"{progressCount} 0", "14 16", "14 22");
                            CuiHelper.AddUi(player, container);
                        }
                        break;

                    #endregion

                    #region UI #2

                    case 2:
                        if (all)
                        {
                            var container = UI.CreateElementContainer("craidcontroller.static.txt", cfg.ui.background, "0.01 0.89", "0.22 0.99");
                            UI.AddText(ref container, "craidcontroller.static.txt", GetTitle(), 14, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.MiddleLeft, "0.05 0.6", "1.0 1");
                            UI.AddText(ref container, "craidcontroller.static.txt", DateTime.UtcNow.AddHours(_timezone).ToString("dddd", dateLang).ToUpper(), 18, "robotocondensed-bold.ttf", "1 1 1 1", TextAnchor.MiddleLeft, "0.05 0", "1.0 1");
                            if (allDayRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidAllDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.MiddleLeft, "0.7 0.10", "1.0 1");
                            else if (allDayNoRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidNoDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.MiddleLeft, "0.7 0.10", "1.0 1");
                            else if (wipeCooldown)
                            {
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Until", player.IPlayer.Id, string.Empty).ToUpper(), 15, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.UpperCenter, "0.7 0", "1.0 0.9");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("dd-MM", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.MiddleCenter, "0.7 0.20", "1.0 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("HH:mm", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.LowerCenter, "0.7 0.30", "1.0 1");
                            }
                            else
                                UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTime(start)} / {GetTime(end)}", 18, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.MiddleCenter, "0.4 0.10", "1.0 1.1");
                            UI.AddImageColor(ref container, "craidcontroller.static.txt", HexToRustFormat(GetColor("blacklight")), "0 0", "1 0", "14 16", "0 22");
                            CuiHelper.AddUi(player, container);
                        }
                        else
                        {
                            var container = UI.CreateElementContainer("craidcontroller.progress.txt", "0 0 0 0", "0.01 0.89", "0.22 0.99");
                            UI.AddImageColor(ref container, "craidcontroller.progress.txt", color, "0 0", $"{progressCount} 0", "14 16", "14 22");
                            CuiHelper.AddUi(player, container);
                        }
                        break;

                    #endregion

                    #region UI #3

                    case 3:
                        if (all)
                        {
                            var container = UI.CreateElementContainer("craidcontroller.static.txt", cfg.ui.background, "0.80 0.92", "0.99 0.99");
                            UI.AddText(ref container, "craidcontroller.static.txt", GetTitle(), 14, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.MiddleLeft, "0.05 0.5", "1.0 1");
                            UI.AddText(ref container, "craidcontroller.static.txt", DateTime.UtcNow.AddHours(_timezone).ToString("dddd", dateLang).ToUpper(), 18, "robotocondensed-bold.ttf", "1 1 1 1", TextAnchor.MiddleLeft, "0.05 0", "1.0 0.8");
                            if (allDayRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidAllDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", HexToRustFormat(GetColor("green")), TextAnchor.MiddleCenter, "0.7 0.10", "1.0 1");
                            else if (allDayNoRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidNoDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", HexToRustFormat(GetColor("red")), TextAnchor.MiddleCenter, "0.7 0.10", "1.0 1");
                            else if (wipeCooldown)
                            {
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Until", player.IPlayer.Id, string.Empty).ToUpper(), 15, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.UpperCenter, "0.7 0", "1.0 0.9");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("dd-MM", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.MiddleCenter, "0.7 0", "1.0 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("HH:mm", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.LowerCenter, "0.7 0.15", "1.0 1");
                            }
                            else
                            {
                                UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTime(start)}", 18, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.UpperCenter, "0.7 0", "1.0 0.95");
                                UI.AddText(ref container, "craidcontroller.static.txt", $"________", 10, "robotocondensed-regular.ttf", "1 1 1 0.3", TextAnchor.MiddleCenter, "0.7 0.24", "1.0 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTime(end)}", 18, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.LowerCenter, "0.7 0.15", "1.0 1");
                            }
                            UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Rest", player.IPlayer.Id), 10, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.LowerLeft, "0.05 0", "1.0 1");
                            UI.AddImageColor(ref container, "craidcontroller.static.txt", HexToRustFormat(GetColor("blacklight")), "0.20 0", "1 0", "10 3", "0 7.5");
                            CuiHelper.AddUi(player, container);
                        }
                        else
                        {
                            var container = UI.CreateElementContainer("craidcontroller.progress.txt", "0 0 0 0", "0.846 0.92", "0.99 0.99");
                            UI.AddImageColor(ref container, "craidcontroller.progress.txt", color, "0 0", $"{progressCount} 0", "0 3.5", "0 7");
                            CuiHelper.AddUi(player, container);
                        }
                        break;

                    #endregion

                    #region UI #4

                    case 4:
                        if (all)
                        {
                            var container = UI.CreateElementContainer("craidcontroller.static.txt", cfg.ui.background, "0.01 0.92", "0.20 0.99");
                            UI.AddText(ref container, "craidcontroller.static.txt", GetTitle(), 14, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.MiddleLeft, "0.05 0.5", "1.0 1");
                            UI.AddText(ref container, "craidcontroller.static.txt", DateTime.UtcNow.AddHours(_timezone).ToString("dddd", dateLang).ToUpper(), 18, "robotocondensed-bold.ttf", "1 1 1 1", TextAnchor.MiddleLeft, "0.05 0", "1.0 0.8");
                            if (allDayRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidAllDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", HexToRustFormat(GetColor("green")), TextAnchor.MiddleCenter, "0.7 0.10", "1.0 1");
                            else if (allDayNoRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidNoDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", HexToRustFormat(GetColor("red")), TextAnchor.MiddleCenter, "0.7 0.10", "1.0 1");
                            else if (wipeCooldown)
                            {
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Until", player.IPlayer.Id, string.Empty).ToUpper(), 15, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.UpperCenter, "0.7 0", "1.0 0.9");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("dd-MM", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.MiddleCenter, "0.7 0", "1.0 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("HH:mm", CultureInfo.InvariantCulture), 13, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.LowerCenter, "0.7 0.15", "1.0 1");
                            }
                            else
                            {
                                UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTime(start)}", 18, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.UpperCenter, "0.7 0", "1.0 0.95");
                                UI.AddText(ref container, "craidcontroller.static.txt", $"________", 10, "robotocondensed-regular.ttf", "1 1 1 0.3", TextAnchor.MiddleCenter, "0.7 0.24", "1.0 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTime(end)}", 18, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.LowerCenter, "0.7 0.15", "1.0 1");
                            }
                            UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Rest", player.IPlayer.Id), 10, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.LowerLeft, "0.05 0", "1.0 1");
                            UI.AddImageColor(ref container, "craidcontroller.static.txt", HexToRustFormat(GetColor("blacklight")), "0.20 0", "1 0", "10 3", "0 7.5");
                            CuiHelper.AddUi(player, container);
                        }
                        else
                        {
                            var container = UI.CreateElementContainer("craidcontroller.progress.txt", "0 0 0 0", "0.056 0.92", "0.20 0.99");
                            UI.AddImageColor(ref container, "craidcontroller.progress.txt", color, "0 0", $"{progressCount} 0", "0 3.5", "0 7");
                            CuiHelper.AddUi(player, container);
                        }
                        break;

                    #endregion

                    #region UI #5

                    case 5:
                        if (all)
                        {
                            var container = UI.CreateElementContainer("craidcontroller.static.txt", cfg.ui.background, "0.371 0.11", "0.615 0.18");
                            UI.AddText(ref container, "craidcontroller.static.txt", GetTitle(), 14, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.MiddleLeft, "0.05 0.5", "1.0 1");
                            UI.AddText(ref container, "craidcontroller.static.txt", DateTime.UtcNow.AddHours(_timezone).ToString("dddd", dateLang).ToUpper(), 18, "robotocondensed-bold.ttf", "1 1 1 1", TextAnchor.MiddleLeft, "0.05 0", "1.0 0.8");
                            if (allDayRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidAllDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", HexToRustFormat(GetColor("green")), TextAnchor.MiddleCenter, "0.6 0.10", "1.0 1");
                            else if (allDayNoRaid)
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("RaidNoDay", player.IPlayer.Id).ToUpper(), 17, "robotocondensed-bold.ttf", HexToRustFormat(GetColor("red")), TextAnchor.MiddleCenter, "0.6 0.10", "1.0 1");
                            else if (wipeCooldown)
                            {
                                UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Until", player.IPlayer.Id, string.Empty).ToUpper(), 17, "robotocondensed-bold.ttf", "1 1 1 0.4", TextAnchor.MiddleLeft, "0.6 0", "1.0 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("dd-MM", CultureInfo.InvariantCulture), 15, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.UpperCenter, "0.8 0", "1.0 0.85");
                                UI.AddText(ref container, "craidcontroller.static.txt", $"________", 10, "robotocondensed-regular.ttf", "1 1 1 0.3", TextAnchor.MiddleCenter, "0.8 0.14", "1 1");
                                UI.AddText(ref container, "craidcontroller.static.txt", GetWipeCd().ToString("HH:mm", CultureInfo.InvariantCulture), 15, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.LowerCenter, "0.8 0.15", "1.0 1");
                            }
                            else
                                UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTime(start)} / {GetTime(end)}", 21, "robotocondensed-regular.ttf", "1 1 1 0.4", TextAnchor.MiddleCenter, "0.4 0", "1.0 1.0");
                            UI.AddText(ref container, "craidcontroller.static.txt", _ins.GetLang("Rest", player.IPlayer.Id), 10, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.LowerLeft, "0.05 0", "1.0 1");
                            UI.AddImageColor(ref container, "craidcontroller.static.txt", HexToRustFormat(GetColor("blacklight")), "0.20 0", "1 0", "10 3", "0 7.5");
                            CuiHelper.AddUi(player, container);
                        }
                        else
                        {
                            var container = UI.CreateElementContainer("craidcontroller.progress.txt", "0 0 0 0", "0.428 0.11", "0.615 0.18");
                            UI.AddImageColor(ref container, "craidcontroller.progress.txt", color, "0 0", $"{progressCount} 0", "0 3.5", "0 7");
                            CuiHelper.AddUi(player, container);
                        }
                        break;

                    #endregion

                    #region UI #6

                    case 6:
                        if (all)
                        {
                            var container = UI.CreateElementContainer("craidcontroller.static.txt", cfg.ui.background, "0.371 0.003", "0.615 0.02");
                            UI.AddText(ref container, "craidcontroller.static.txt", $"{GetTitle().ToUpper()}  • {_ins.GetLang("Rest", player.IPlayer.Id)}", 10, "robotocondensed-regular.ttf", "1 1 1 0.6", TextAnchor.LowerLeft, "-0.1 0", "1.0 1");
                            UI.AddImageColor(ref container, "craidcontroller.static.txt", HexToRustFormat(GetColor("blacklight")), "0.40 0", "1 0", "10 3", "0 7.5");
                            CuiHelper.AddUi(player, container);
                        }
                        else
                        {
                            var container = UI.CreateElementContainer("craidcontroller.progress.txt", "0 0 0 0", "0.477 0.0035", "0.615 0.02");
                            UI.AddImageColor(ref container, "craidcontroller.progress.txt", color, "0 0", $"{progressCount} 0", "0 3.5", "0 6");
                            CuiHelper.AddUi(player, container);
                        }
                        break;

                    #endregion

                    default:
                        goto case 1;
                }
            }
            private void DestroyUI(bool all = false)
            {
                int uiChoice = 0;

                if (!pConf.ContainsKey(player.userID))
                    uiChoice = cfg.ui.uiChoice;
                else
                    if (pConf[player.userID].choose > 0 && pConf[player.userID].choose <= _uiCount)
                    uiChoice = pConf[player.userID].choose;
                else
                    uiChoice = cfg.ui.uiChoice;

                if (all)
                {
                    CuiHelper.DestroyUi(player, "craidcontroller.static.txt");
                    CuiHelper.DestroyUi(player, "craidcontroller.progress.txt");
                }
                else
                    CuiHelper.DestroyUi(player, "craidcontroller.progress.txt");
            }
            private void OnDestroy() => DestroyUI(true);
        }

        #endregion

        #region Function         
        internal List<string> shortPrefabName = new List<string>()
                {
                    "ammo.rifle.incendiary",
                    "ammo.rifle.explosive",
                    "ammo.pistol.fire",
                    "ammo.shotgun.fire"
                };

        public void RemoveCd(string t, ulong i)
        {
            if (!pCd.ContainsKey($"{t}:{i}"))
                return;

            timer.In(pCd[$"{t}:{i}"], () =>
            {
                pCd.Remove(($"{t}:{i}"));
            });
        }
        private void Nullify(HitInfo info)
        {
            info.damageTypes = new DamageTypeList();
            info.HitEntity = null;
            info.HitMaterial = 0;
            info.PointStart = Vector3.zero;
        }
        private void CatchFireball(BasePlayer player, BaseCombatEntity entity, HitInfo info)
        {
            var weapon = player.GetHeldEntity() as BaseProjectile;

            if (weapon == null)
                return;

            foreach (var x in shortPrefabName.Where(x => x == weapon.primaryMagazine.ammoType.shortname))
            {
                if (!fireBall.ContainsKey(DateTime.Now.TimeOfDay))
                    fireBall.Add(DateTime.Now.TimeOfDay, new FireBallList() { entity = entity.net.ID, player = player.userID, radius = info.HitPositionWorld });
            }
        }

        internal Dictionary<string, string[]> prefabList = new Dictionary<string, string[]>()
                    {
                        {"explosive.timed", new string[]{"explosive.timed.deployed","explosive"}},
                        {"explosive.satchel", new string[]{"explosive.satchel.deployed","explosive"}},
                        {"grenade.beancan", new string[]{"grenade.beancan.deployed","grenade"}},
                        {"grenade.f1", new string[]{"grenade.f1.deployed","grenade"}},
                        {"grenade.molotov", new string[]{"grenade.molotov.deployed","grenade"}},

                        {"ammo.rocket.fire", new string[]{"rocket_fire","rocket"}},
                        {"ammo.rocket.hv", new string[]{"rocket_hv","rocket"}},
                        {"ammo.rocket.basic", new string[]{"rocket_basic","rocket"}},
                        {"ammo.grenadelauncher.he", new string[]{"40mm_grenade_he","grenadelauncher"}},
                        {"ammo.rifle", new string[]{"riflebullet","rifle"}},
                        {"ammo.rifle.explosive", new string[]{"riflebullet_explosive","rifle"}},
                        {"ammo.rifle.incendiary", new string[]{"riflebullet_fire","rifle"}},
                        {"ammo.pistol", new string[]{"pistolbullet","pistol"}},
                        {"ammo.pistol.fire", new string[]{"pistolbullet_fire","pistol"}},
                        {"ammo.shotgun", new string[]{"shotgunbullet","shotgun"}},
                        {"ammo.shotgun.fire", new string[]{"shotgunbullet_fire","shotgun"}},
                        {"ammo.shotgun.slug", new string[]{"shotgunslug","shotgun"}},
                    };

        internal List<string> explosiveList = new List<string>()
                    {
                        "rocket_fire",
                        "rocket_hv",
                        "rocket_basic",
                        "explosive.timed.deployed",
                        "survey_charge.deployed",
                        "explosive.satchel.deployed",
                        "grenade.beancan.deployed",
                        "grenade.f1.deployed",
                        "grenade.molotov.deployed",
                        "40mm_grenade_he"
                    };

        private void Refund(BasePlayer player, HitInfo info)
        {
            if (cfg.config.refundAmmo.enabled)
            {
                string name = string.Empty;

                if (explosiveList.Contains(info?.WeaponPrefab?.ShortPrefabName))
                    name = info?.WeaponPrefab?.ShortPrefabName;
                else
                    name = info?.ProjectilePrefab?.name.ToString();

                foreach (var x in prefabList)
                {
                    if (name == x.Value[0])
                    {
                        if (!cfg.config.refundAmmo.list.explosive && x.Value[1] == "explosive")
                            return;
                        if (!cfg.config.refundAmmo.list.grenade && x.Value[1] == "grenade")
                            return;
                        if (!cfg.config.refundAmmo.list.rocket && x.Value[1] == "rocket")
                            return;
                        if (!cfg.config.refundAmmo.list.grenadelauncher && x.Value[1] == "grenadelauncher")
                            return;
                        if (!cfg.config.refundAmmo.list.rifle && x.Value[1] == "rifle")
                            return;
                        if (!cfg.config.refundAmmo.list.pistol && x.Value[1] == "pistol")
                            return;
                        if (!cfg.config.refundAmmo.list.shotgun && x.Value[1] == "shotgun")
                            return;

                        if (pCd.ContainsKey($"refund:{player.userID}"))
                            return;

                        Item item = ItemManager.CreateByName(x.Key, 1);
                        player.GiveItem(item);
                        pCd.Add($"refund:{player.userID}", 0.1f);
                        RemoveCd("refund", player.userID);
                    }
                }
            }
        }
        private bool TakeDamage(BasePlayer player, BaseCombatEntity entity, HitInfo info, bool canRaid, bool wipeCooldown)
        {
            if (wipeCooldown)
                canRaid = false;

            if (!canRaid && (entity is BuildingBlock || entity is SimpleBuildingBlock || entity is Door || entity is ShopFront || entity.PrefabName.Contains("deploy")))
            {
                if (cfg.config.isAdmin && player.IsAdmin)
                    return true;


                if (cfg.config.isTwigs && entity is BuildingBlock)
                {
                    BuildingBlock ent = entity as BuildingBlock;
                    if (ent.grade.ToString() == "Twigs")
                        return true;
                }

                if (cfg.config.isOwner && IsOwner(player.userID, entity.OwnerID))
                    return true;

                if (cfg.config.isNoOwner && IsNoOwner(entity.OwnerID))
                    return true;

                if (cfg.config.itemBypass.Contains(entity.PrefabName) && !IsTcProtected(entity, info))
                    return true;

                if (cfg.config.itemBypassTcProtect.Contains(entity.PrefabName) && IsTcProtected(entity, info))
                    return true;

                if (cfg.config.isDecay && IsDecay(entity, info) && IsTcProtected(entity, info))
                    return true;

                if (cfg.config.isEmpty && IsDecay(entity, info) && !IsTcProtected(entity, info))
                    return true;

                if (cfg.config.isMate && IsMate(player, entity.OwnerID))
                    return true;

                if (entity.OwnerID == null || entity.OwnerID == 0)
                    return true;

                return false;
            }
            return true;
        }

        #endregion

        #region UI
        static class UI
        {
            static public CuiElementContainer CreateElementContainer(string panel, string color, string aMin, string aMax)
            {
                var NewElement = new CuiElementContainer()
                    {
                        {
                            new CuiPanel
                            {
                                Image = {Color = color},
                                RectTransform = {AnchorMin = aMin, AnchorMax = aMax}
                            },
                            new CuiElement().Parent = "Hud",
                            panel
                        }
                    };
                return NewElement;
            }
            static public void AddText(ref CuiElementContainer container, string panel, string text, int size, string font, string color, TextAnchor align, string aMin, string aMax)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                        {
                            new CuiTextComponent
                            {
                                Text =  text, FontSize = size,
                                Font = font,
                                Color = color,
                                Align = align
                            },
                            new CuiRectTransformComponent {
                                AnchorMin = aMin,
                                AnchorMax = aMax
                            }
                        }
                });
            }
            static public void AddImageColor(ref CuiElementContainer container, string panel, string color, string aMin, string aMax, string oMin, string oMax)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                        {
                            new CuiImageComponent
                            {
                                Color = color
                            },
                            new CuiRectTransformComponent {
                                AnchorMin = aMin,
                                AnchorMax = aMax,
                                OffsetMin = oMin,
                                OffsetMax = oMax
                            }

                        }
                });
            }
        }

        #endregion

        #region Effect

        private static void SendEffect(BasePlayer player, string _sound)
        {
            var effect = new Effect(_sound, player, 0, Vector3.zero, Vector3.forward);
            EffectNetwork.Send(effect, player.net.connection);
        }

        #endregion

        #region Config
        private static ConfigData cfg;
        private class ConfigData
        {
            [JsonProperty("1. CONFIG")]
            public ConfigList config { get; set; }

            [JsonProperty("2. UI")]
            public UIList ui { get; set; }

            [JsonProperty("3. RAID SCHEDULES")]
            public Dictionary<string, RaidList> raid { get; set; }
        }
        private class ConfigList
        {
            [JsonProperty(PropertyName = "• Set your TimeZone UTC")]
            public string timezone { get; set; }

            [JsonProperty("• Enable nullify damage")]
            public bool nullify { get; set; }

            [JsonProperty("• Entity's owner bypass")]
            public bool isOwner { get; set; }

            [JsonProperty("• Entity's no owner bypass")]
            public bool isNoOwner { get; set; }

            [JsonProperty("• Player fireball bypass")]
            public bool playerFire { get; set; }

            [JsonProperty("• Admin bypass")]
            public bool isAdmin { get; set; }

            [JsonProperty("• Team mate bypass")]
            public bool isMate { get; set; }

            [JsonProperty("• Twigs grade bypass")]
            public bool isTwigs { get; set; }

            [JsonProperty("• Cupboard decay base bypass")]
            public bool isDecay { get; set; }

            [JsonProperty("• No Cupboard base bypass")]
            public bool isEmpty { get; set; }

            [JsonProperty("• Item Bypass cupboard protected (prefab name)")]
            public List<string> itemBypassTcProtect { get; set; }

            [JsonProperty("• Item Bypass (prefab name)")]
            public List<string> itemBypass { get; set; }

            [JsonProperty("• Return ammo if not time")]
            public RefundConfig refundAmmo { get; set; }

            [JsonProperty("• Message config")]
            public MessageConfig message { get; set; }

            [JsonProperty("• Message before the raid time")]
            public MessageBeforeConfig alertMessage { get; set; }

            [JsonProperty("• Message when raids are allowed")]
            public MessageTimeConfig timeMessage { get; set; }

            [JsonProperty("• Message when raids are finished")]
            public MessageTimeConfig timeFinishMessage { get; set; }

            [JsonProperty("• Message when player try to damage")]
            public MessageTimeConfig tryDamageMessage { get; set; }
        }
        private class RefundConfig
        {
            [JsonProperty(PropertyName = "» Enabled")]
            public bool enabled { get; set; }

            [JsonProperty(PropertyName = "» Ammunition")]
            public RefundListConfig list { get; set; }
        }
        private class RefundListConfig
        {
            [JsonProperty(PropertyName = "» Explosive")]
            public bool explosive { get; set; }

            [JsonProperty(PropertyName = "» Grenade")]
            public bool grenade { get; set; }

            [JsonProperty(PropertyName = "» Rocket")]
            public bool rocket { get; set; }

            [JsonProperty(PropertyName = "» Grenade (40mm Launcher)")]
            public bool grenadelauncher { get; set; }

            [JsonProperty(PropertyName = "» Rifle")]
            public bool rifle { get; set; }

            [JsonProperty(PropertyName = "» Pistol")]
            public bool pistol { get; set; }

            [JsonProperty(PropertyName = "» Shotgun")]
            public bool shotgun { get; set; }
        }
        private class MessageConfig
        {
            [JsonProperty(PropertyName = "» Prefix")]
            public string prefix { get; set; }

            [JsonProperty(PropertyName = "» Icon")]
            public ulong icon { get; set; }
        }
        private class MessageBeforeConfig
        {
            [JsonProperty(PropertyName = "» Enabled")]
            public bool enabled { get; set; }

            [JsonProperty(PropertyName = "» Effect")]
            public bool effect { get; set; }

            [JsonProperty(PropertyName = "» Every x min")]
            public int countdown { get; set; }
        }
        private class MessageTimeConfig
        {
            [JsonProperty(PropertyName = "» Enabled")]
            public bool enabled { get; set; }

            [JsonProperty(PropertyName = "» Effect")]
            public bool effect { get; set; }
        }
        private class UIList
        {
            [JsonProperty("• Hide UI")]
            public bool hide { get; set; }

            [JsonProperty("• Hide if isn't raid time")]
            public bool hideIfNotTime { get; set; }

            [JsonProperty("• Hide if is raid time")]
            public bool hideIsTime { get; set; }

            [JsonProperty("• Allow player to hide")]
            public bool allowPlayerhide { get; set; }

            [JsonProperty("• Allow player to change")]
            public bool allowPlayerChange { get; set; }

            [JsonProperty("• Chat command")]
            public string command { get; set; }

            [JsonProperty("• Refresh rate")]
            public float rate { get; set; }

            [JsonProperty("• Background")]
            public string background { get; set; }

            [JsonProperty("• Select interface (see readme)")]
            public int uiChoice { get; set; }

            [JsonProperty("• Enable Time12 for US")]
            public bool time12 { get; set; }
        }
        private class RaidList
        {
            [JsonProperty("• Wipe Cooldown")]
            public WipeConfig wipeCooldown { get; set; }

            [JsonProperty("• Monday")]
            public DayConfig monday { get; set; }

            [JsonProperty("• Tuesday")]
            public DayConfig tuesday { get; set; }

            [JsonProperty("• Wednesday")]
            public DayConfig wednesday { get; set; }

            [JsonProperty("• Thursday")]
            public DayConfig thursday { get; set; }

            [JsonProperty("• Friday")]
            public DayConfig friday { get; set; }

            [JsonProperty("• Saturday")]
            public DayConfig saturday { get; set; }

            [JsonProperty("• Sunday")]
            public DayConfig sunday { get; set; }
        }
        private class WipeConfig
        {
            [JsonProperty(PropertyName = "» Enabled")]
            public bool enabled { get; set; }

            [JsonProperty(PropertyName = "» Days after wipe")]
            public int days { get; set; }

            [JsonProperty(PropertyName = "» Custom (dd-MM-yy HH:mm)")]
            public string custom { get; set; }
        }
        private class DayConfig
        {
            [JsonProperty("» All day raid")]
            public bool allday { get; set; }

            [JsonProperty("» No raid all day")]
            public bool noallday { get; set; }

            [JsonProperty("» Schedules")]
            public List<SchedulesConfig> schedules { get; set; }
        }
        private class SchedulesConfig
        {
            [JsonProperty("» Start")]
            public string start { get; set; }

            [JsonProperty("» End")]
            public string end { get; set; }
        }
        private ConfigData GetDefaultConfig()
        {
            return new ConfigData
            {
                config = new ConfigList
                {
                    timezone = "+2",
                    nullify = true,
                    isOwner = true,
                    isNoOwner = true,
                    isAdmin = true,
                    isMate = true,
                    isTwigs = true,
                    isDecay = false,
                    isEmpty = false,
                    itemBypassTcProtect = new List<string>
                        {
                            "assets/prefabs/deployable/campfire/campfire.prefab"
                        },
                    itemBypass = new List<string>
                        {
                            "assets/prefabs/deployable/sleeping bag/sleepingbag_leather_deployed.prefab"
                        },
                    refundAmmo = new RefundConfig
                    {
                        enabled = false,
                        list = new RefundListConfig
                        {
                            explosive = true,
                            grenade = true,
                            rocket = true,
                            grenadelauncher = true,
                            rifle = true,
                            pistol = true,
                            shotgun = true
                        },
                    },
                    message = new MessageConfig
                    {
                        prefix = "<color=#aabf7d>• SERVER</color> <size=18>»</size> <color=#ebd077>Raid Controller</color> : ",
                        icon = 0
                    },
                    alertMessage = new MessageBeforeConfig
                    {
                        enabled = true,
                        effect = true,
                        countdown = 30,
                    },
                    timeMessage = new MessageTimeConfig
                    {
                        enabled = true,
                        effect = true,
                    },
                    timeFinishMessage = new MessageTimeConfig
                    {
                        enabled = true,
                        effect = true,
                    },
                    tryDamageMessage = new MessageTimeConfig
                    {
                        enabled = true,
                        effect = true,
                    }
                },
                ui = new UIList
                {
                    hide = false,
                    hideIfNotTime = false,
                    hideIsTime = false,
                    allowPlayerhide = false,
                    allowPlayerChange = false,
                    command = "craid",
                    rate = 1f,
                    background = "0 0 0 0.7",
                    uiChoice = 1,
                    time12 = true
                },
                raid = new Dictionary<string, RaidList>
                    {
                        {
                            "default", new RaidList
                            {
                                wipeCooldown = new WipeConfig
                                {
                                    enabled = false,
                                    days = 0,
                                    custom = null
                                },
                                monday = new DayConfig
                                {
                                    allday = false,
                                    noallday = false,
                                    schedules = new List<SchedulesConfig>
                                    {
                                        new SchedulesConfig
                                        {
                                            start = "08:00",
                                            end = "10:00"
                                        },
                                        new SchedulesConfig
                                        {
                                            start = "16:00",
                                            end = "00:00"
                                        }
                                    }
                                },
                                tuesday = new DayConfig
                                {
                                    schedules = new List<SchedulesConfig>
                                    {
                                        new SchedulesConfig
                                        {
                                            start = "16:00",
                                            end = "00:00"
                                        }
                                    }
                                },
                                wednesday = new DayConfig
                                {
                                    schedules = new List<SchedulesConfig>
                                    {
                                        new SchedulesConfig
                                        {
                                            start = "16:00",
                                            end = "00:00"
                                        }
                                    }
                                },
                                thursday = new DayConfig
                                {
                                    schedules = new List<SchedulesConfig>
                                    {
                                        new SchedulesConfig
                                        {
                                            start = "16:00",
                                            end = "00:00"
                                        }
                                    }
                                },
                                friday = new DayConfig
                                {
                                    schedules = new List<SchedulesConfig>
                                    {
                                        new SchedulesConfig
                                        {
                                            start = "16:00",
                                            end = "00:00"
                                        }
                                    }
                                },
                                saturday = new DayConfig
                                {
                                    schedules = new List<SchedulesConfig>
                                    {
                                        new SchedulesConfig
                                        {
                                            start = "16:00",
                                            end = "00:00"
                                        }
                                    }
                                },
                                sunday = new DayConfig
                                {
                                    schedules = new List<SchedulesConfig>
                                    {
                                        new SchedulesConfig
                                        {
                                            start = "16:00",
                                            end = "00:00"
                                        }
                                    }
                                },
                            }
                        }
                    }
            };
        }
        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                cfg = Config.ReadObject<ConfigData>();

                if (cfg == null)
                    LoadDefaultConfig();
            }
            catch
            {
                PrintError("Configuration file is corrupt! If you have just upgraded to version 3.0.0, delete the configuration file and reload. Else if check your config file at https://jsonlint.com/");

                LoadDefaultConfig();
                return;
            }

            SaveConfig();
        }
        protected override void LoadDefaultConfig() => cfg = GetDefaultConfig();
        protected override void SaveConfig() => Config.WriteObject(cfg);

        #endregion

        #region Data
        private Dictionary<ulong, RaidController> _controller;
        private Dictionary<TimeSpan, FireBallList> fireBall = new Dictionary<TimeSpan, FireBallList>();
        private static Dictionary<ulong, PlayerConf> pConf = new Dictionary<ulong, PlayerConf>();
        private void LoadData() => pConf = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerConf>>("CRaidController/playersConf");
        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject("CRaidController/playersConf", pConf);
        private static Dictionary<string, string> color = new Dictionary<string, string>()
            {
                {"green", "#aabf7dFF"},
                {"red", "#d8453dF1"},
                {"blacklight", "#00FFFFFF"},
            };
        private Dictionary<string, float> pCd = new Dictionary<string, float>();
        private static List<ulong> needRefresh = new List<ulong>();
        private class FireBallList
        {
            public uint entity;
            public ulong player;
            public Vector3 radius;
            public FireBallList() { }
        }
        private class PlayerConf
        {
            public bool hide = false;
            public int choose = 0;
            public PlayerConf() { }
        }
        private class SchedulesList
        {
            public string start;
            public string end;
            public bool allday;
            public bool noallday;
            public SchedulesList() { }
        }

        #endregion

        #region Command
        private void PlayerConfCommand(BasePlayer player, string command, string[] args)
        {
            if (!cfg.ui.hide)
            {
                if (!pConf.ContainsKey(player.userID))
                    pConf.Add(player.userID, new PlayerConf());

                if (args.Count() == 0)
                {
                    Message(player, "SyntaxError", cfg.ui.command, _uiCount);
                    return;
                }

                if (args[0] == "hide")
                {
                    if (!player.IsAdmin && !cfg.ui.allowPlayerhide && !player.IPlayer.HasPermission(_permHide))
                    {
                        Message(player, "NotPermsHide");
                        return;
                    }
                    if (pConf[player.userID].hide)
                    {
                        pConf[player.userID].hide = false;
                        Message(player, "UIOn");
                    }
                    else
                    {
                        pConf[player.userID].hide = true;
                        Message(player, "UIOff");
                    }
                }

                int num;
                if (int.TryParse(args[0], out num))
                {
                    if (!player.IsAdmin && !cfg.ui.allowPlayerChange && !player.IPlayer.HasPermission(_permChange))
                    {
                        Message(player, "NotPermsChange");
                        return;
                    }

                    if (num > _uiCount || num == 0)
                    {
                        Message(player, "SyntaxErrorChoose", _uiCount);
                        return;
                    }

                    pConf[player.userID].choose = num;
                    Message(player, "UIChanged", num);
                }

                SaveData();
                LoadData();
                API_RefreshUI(player);
            }
        }

        #endregion

        #region Localization
        protected override void LoadDefaultMessages()
        {
            //English
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["AlertMessage"] = "Raids will be authorized in <color=#87ceeb>{0} hour(s)</color> and <color=#87ceeb>{1} minute(s)</color>.",
                ["TimeMessage"] = "Raids are allowed up to <color=#87ceeb>{0}</color>.",
                ["FinishMessage"] = "Raids completed, they will be allowed at <color=#87ceeb>{0}</color>.",
                ["FinishTodayMessage"] = "Raids finished for today",
                ["RaidTime"] = "• Raid allowed",
                ["NoRaidTime"] = "• Raids not allowed",
                ["NoRaidCooldown"] = "• Raids not allowed",
                ["Rest"] = "Remaining →",
                ["Finish"] = "Finish",
                ["Until"] = "Until {0}",
                ["RaidAllDay"] = "All the day",
                ["RaidNoDay"] = "No raids today",
                ["UIOn"] = "Display <color=#90ee90>activated</color>",
                ["UIOff"] = "Display <color=#f08080>disabled</color>",
                ["UIChanged"] = "Display <color=#90ee90>[{0}]</color>",
                ["SyntaxError"] = "Syntax error : <br><br>"
                                + "<color=#87ceeb>/{0} hide</color> : Hide / View display<br>"
                                + "<color=#87ceeb>/{0} 1-{1}</color> : Choose display<br>",
                ["SyntaxErrorChoose"] = "The value must be between 1 and {0}",
                ["NotPermsHide"] = "You don't have permission to hide display",
                ["NotPermsChoose"] = "You do not have permission to change display",
            }, this);
            //French
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["AlertMessage"] = "Les raids seront autorisés dans <color=#87ceeb>{0} heure(s)</color> et <color=#87ceeb>{1} minute(s)</color>.",
                ["TimeMessage"] = "Les raids sont autorisés jusqu’à <color=#87ceeb>{0}</color>.",
                ["FinishMessage"] = "Les raids sont terminés, ils seront autorisés à <color=#87ceeb>{0}</color>.",
                ["FinishTodayMessage"] = "Les raids sont terminés pour aujourd'hui.",
                ["RaidTime"] = "• Raid autorisé",
                ["NoRaidTime"] = "• Raid interdit",
                ["NoRaidCooldown"] = "• Raid interdit",
                ["Rest"] = "Restant →",
                ["Finish"] = "Terminé",
                ["Until"] = "Jusqu'au {0}",
                ["RaidAllDay"] = "Toute la journée",
                ["RaidNoDay"] = "Pas de raids",
                ["UIOn"] = "Affichage <color=#90ee90>activé</color>",
                ["UIOff"] = "Affichage <color=#f08080>désactivé</color>",
                ["UIChanged"] = "Affichage <color=#90ee90>[{0}]</color>",
                ["SyntaxError"] = "Erreur de syntaxe : <br><br>"
                                + "<color=#87ceeb>/{0} hide</color> : Cacher / Afficher l'affichage<br>"
                                + "<color=#87ceeb>/{0} 1-{1}</color> : Changer l'affichage<br>",
                ["SyntaxErrorChoose"] = "La valeur doit être entre 1 et {0}",
                ["NotPermsHide"] = "Vous n'avez pas l’autorisation de masquer l'affichage",
                ["NotPermsChoose"] = "Vous n'avez pas l’autorisation de changer l'affichage",
            }, this, "fr");
        }

        #endregion

        #region Helpers
        private string GetLang(string langKey, string playerId = null, params object[] args) => string.Format(lang.GetMessage(langKey, this, playerId), args);
        private static void Message(BasePlayer player, string langKey, params object[] args)
        {
            if (player.IsConnected)
                _ins.Player.Message(player, cfg.config.message.prefix + _ins.GetLang(langKey, player.IPlayer.Id, args), cfg.config.message.icon);
        }
        private static string HexToRustFormat(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                hex = "#FFFFFFFF";

            var str = hex.Trim('#');

            if (str.Length == 6)
                str += "FF";

            if (str.Length != 8)
            {
                throw new Exception(hex);
                throw new InvalidOperationException("Cannot convert a wrong format.");
            }

            var r = byte.Parse(str.Substring(0, 2), NumberStyles.HexNumber);
            var g = byte.Parse(str.Substring(2, 2), NumberStyles.HexNumber);
            var b = byte.Parse(str.Substring(4, 2), NumberStyles.HexNumber);
            var a = byte.Parse(str.Substring(6, 2), NumberStyles.HexNumber);

            Color color = new Color32(r, g, b, a);

            return string.Format("{0:F2} {1:F2} {2:F2} {3:F2}", color.r, color.g, color.b, color.a);
        }
        private static TimeSpan ToTimeSpan(string time) => TimeSpan.Parse(time);
        private static string ToTime24(string time)
        {
            if (time == "23:59:59")
                return "00:00";

            return time;
        }
        private static string ToTime12(string time)
        {
            if (time == "23:59:59")
                return "00:00 AM";

            var ret = DateTime.ParseExact(time, "H:m", null, DateTimeStyles.None);
            return ret.ToString("hh:mm tt", CultureInfo.InvariantCulture);
        }
        private static bool CheckInGroup(BasePlayer player, string name) => _ins.permission.UserHasGroup(player.IPlayer.Id, name);
        public bool IsOwner(ulong initiatorID, ulong entityOwnerID) => initiatorID == entityOwnerID;
        public bool IsNoOwner(ulong entityOwnerID) => entityOwnerID == 0;
        private bool IsMate(BasePlayer player, ulong target)
        {
            if (IsOwner(player.userID, target))
                return false;

            if (player.Team == null)
                return false;

            if (player.Team.members.Contains(target))
                return true;

            return false;
        }
        private bool IsTcProtected(BaseEntity entity, HitInfo info)
        {
            if (entity is BuildingBlock)
            {
                BuildingBlock block = entity as BuildingBlock;
                BuildingManager.Building building = block.GetBuilding();

                if (building != null)
                {
                    if (building.GetDominatingBuildingPrivilege() == null)
                        return false;

                    return true;
                }

                return false;
            }
            else
            {
                int targetLayer = LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed");
                Collider[] hit = Physics.OverlapSphere(entity.transform.position, _cupboardRadius, targetLayer);

                foreach (var ent in hit)
                {
                    BuildingPrivlidge privs = ent.GetComponentInParent<BuildingPrivlidge>();
                    if (privs != null)
                        return true;
                }

                if (hit.Length > 0)
                    return false;

                return true;
            }
        }
        private bool IsDecay(BaseEntity entity, HitInfo info)
        {
            BuildingPrivlidge priv = entity.GetBuildingPrivilege();

            if (priv == null)
            {
                priv = entity.GetComponentInParent<BuildingPrivlidge>();
                if (priv == null)
                    return true;
            }

            float minutesLeft = priv.GetProtectedMinutes();

            if (minutesLeft == 0)
                return true;

            return false;
        }
        private bool IsNear(Vector3 a, Vector3 b)
        {
            var distance = Vector3.Distance(a, b);

            if (distance < _radiusFireball)
                return true;

            return false;
        }

        #endregion

        #region API
        private void API_RefreshUI(BasePlayer player)
        {
            if (!needRefresh.Contains(player.userID))
                needRefresh.Add(player.userID);
        }

        #endregion
    }
}
