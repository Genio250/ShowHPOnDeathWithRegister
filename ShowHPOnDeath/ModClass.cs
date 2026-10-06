using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InControl;
using Modding;
using Modding.Converters;
using Newtonsoft.Json;
using UnityEngine;
using static MonoMod.Cil.RuntimeILReferenceBag.FastDelegateInvokers;


namespace ShowHPOnDeath
{
    public class KeyBinds : PlayerActionSet
    {
        //the keybinds you want to save. it needs to be of type PlayerAction
        public PlayerAction Hide;

        //a constructor to initalize the PlayerAction
        public KeyBinds()
        {
            Hide = CreatePlayerAction("Hide");

            //optional: set a default bind
            Hide.AddDefaultBinding(Key.H);
        }
    }

    [Serializable]
    public class GlobalSettings
    {
        public bool EnabledMod = true;
        public bool ShowPB = true;
        public bool HideAfter10Sec = true;
        [JsonConverter(typeof(PlayerActionSetConverter))]
        public KeyBinds keybinds = new KeyBinds();
    }

    public class ShowHPOnDeath : Mod, IMenuMod, IGlobalSettings<GlobalSettings>
    {
        // ==== Подготовкаа ====
        public static GlobalSettings GS { get; private set; } = new();
        public void OnLoadGlobal(GlobalSettings s) => GS = s;
        public GlobalSettings OnSaveGlobal() => GS;

        public ShowHPOnDeath() : base("ShowHPOnDeath") { }
        public override string GetVersion() => "1.3.0";

        private static List<(string Name, int HP)> CurrentBosses = new List<(string, int)>();
        private static string LastDisplayText = "";
        private static Dictionary<string, int> InitialHPs = new();
        private static int PersonalBest = int.MaxValue;
        private static string LastBossName = "";
        private static CancellationTokenSource currentDisplayToken = null;

        public override void Initialize()
        {
            On.HealthManager.OnEnable += OnHealthManagerEnable;
            ModHooks.BeforeSceneLoadHook += BeforeSceneLoad;
            ModHooks.HeroUpdateHook += OnHeroUpdate;
            CreateUI();
        }


        public void OnHeroUpdate()
        {
            if (GS.keybinds.Hide.WasPressed)
            {
                UpdateDisplay("");
            }
        }

        // ==== Реализация меню (без satchel btw) ====

        bool IMenuMod.ToggleButtonInsideMenu => true;

        public bool ToggleButtonInsideMenu => true;

        List<IMenuMod.MenuEntry> IMenuMod.GetMenuData(IMenuMod.MenuEntry? toggleButtonEntry)
        {
            return new List<IMenuMod.MenuEntry>
    {
        new IMenuMod.MenuEntry
        {
            Name = "Global Switch",
            Description = "Turn mod On/Off",
            Values = new[] { "Off", "On" },
            Saver = opt => ChangeEnabled(opt == 1),
            Loader = () => GS.EnabledMod ? 1 : 0
        },
        new IMenuMod.MenuEntry
        {
            Name = "Show PB",
            Description = "Show or hide Personal Best",
            Values = new[] { "Off", "On" },
            Saver = opt => ChangeShowPB(opt == 1),
            Loader = () => GS.ShowPB ? 1 : 0
        },
        new IMenuMod.MenuEntry
        {
            Name = "Auto-Hide",
            Description = "Hides the display after a 10 seconds",
            Values = new[] { "Off", "On" },
            Saver = opt => ChangeHide(opt == 1),
            Loader = () => GS.HideAfter10Sec ? 1 : 0
        }
    };
        }

        private void ChangeEnabled(bool state)
        {
            GS.EnabledMod = state;
        }

        private void ChangeShowPB(bool state)
        {
            GS.ShowPB = state;
        }
        private void ChangeHide(bool state)
        {
            GS.HideAfter10Sec = state;
        }


        // ==== Логика добавления босса после того как сдох ====
        private string BeforeSceneLoad(string newSceneName)
        {
            if (!GS.EnabledMod)
            {
                CurrentBosses.Clear();
                UpdateDisplay("");
                InitialHPs.Clear();
                return newSceneName;
            }

            // Отменяем предыдущий таймер
            currentDisplayToken?.Cancel();
            currentDisplayToken?.Dispose();
            currentDisplayToken = new CancellationTokenSource();

            CurrentBosses.Clear();
            bool foundAnyBoss = false;

            foreach (var boss in UnityEngine.Object.FindObjectsOfType<HealthManager>())
            {
                if (boss != null && boss.gameObject.activeInHierarchy && boss.hp > 0)
                {
                    if (BossNames.TryGetValue(boss.gameObject.name, out string displayName))
                    {
                        string filepath = Path.Combine(
                            "hollow_knight_Data",
                            "Managed",
                            "Mods",
                            "ShowHPOnDeathHistory",
                            "Register.tsv"
                        );
                        string saving = displayName + '\t' + boss.hp + '\n';
                        File.AppendAllText(filepath, saving);

                        CurrentBosses.Add((displayName, boss.hp));
                        foundAnyBoss = true;

                        // === ЛОГИКА PB ===
                        if (displayName == LastBossName)
                        {
                            if (boss.hp < PersonalBest)
                            {
                                PersonalBest = boss.hp;
                            }
                        }
                        else
                        {
                            PersonalBest = boss.hp;
                            LastBossName = displayName;
                        }
                    }
                }
            }

            // Формируем текст
            string displayText = "";
            if (foundAnyBoss)
            {
                displayText += $"Press [{GS.keybinds.Hide.UnfilteredBindings[0].Name}] to hide\n⸻⸻⸻\n";
            }

            for (int i = 0; i < CurrentBosses.Count; i++)
            {
                var (name, currentHP) = CurrentBosses[i];
                int count = CurrentBosses.Take(i).Count(b => b.Name == name);
                string finalName = count > 0 ? $"{name} ({count + 1})" : name;

                if (InitialHPs.TryGetValue(name, out int initialHP))
                {
                    string pbText = PersonalBest == int.MaxValue ? "-" : PersonalBest.ToString();
                    displayText += $"[{finalName}]\nHP: {currentHP} / {initialHP}\n";
                    if (GS.ShowPB)
                    {
                        displayText += $"PB: {pbText}\n";
                    }
                }
                else
                {
                    displayText += $"[{finalName}]\nHP: {currentHP}\n";
                }
            }

            LastDisplayText = displayText.Trim();
            UpdateDisplay(displayText);

            // Запускаем новый таймер с отменой
            if (GS.HideAfter10Sec)
            {
                Task.Delay(10000, currentDisplayToken.Token)
                    .ContinueWith(_ =>
                    {
                        if (!_.IsCanceled)
                            UpdateDisplay("");
                    }, TaskScheduler.Default);
            }

            return newSceneName;
        }




        // ==== Ну тут понятно получение хп ====
        private void OnHealthManagerEnable(On.HealthManager.orig_OnEnable orig, HealthManager self)
        {
            if (!GS.EnabledMod) return;
            if (BossNames.TryGetValue(self.gameObject.name, out string displayName))
            {
                if (!InitialHPs.ContainsKey(displayName))
                {
                    Task.Delay(1000).ContinueWith(_x =>
                    {
                        HealthManager HM = self.gameObject.GetComponent<HealthManager>();
                        if (HM != null)
                        {
                            InitialHPs[displayName] = HM.hp;
                        }
                    });
                }

                if (!CurrentBosses.Any(b => b.Name == displayName))
                {
                    Task.Delay(1000).ContinueWith(_x =>
                    {
                        HealthManager HM = self.gameObject.GetComponent<HealthManager>();
                        if (HM != null)
                        {
                            CurrentBosses.Add((displayName, HM.hp));
                        }
                    });
                }
            }
            orig(self);
        }

        // ==== Отображение информации ====
        private void CreateUI()
        {
            if (ModDisplay.Instance == null)
            {
                ModDisplay.Instance = new ModDisplay();
            }
            else
            {
                ModDisplay.Instance.Destroy();
                ModDisplay.Instance = new ModDisplay();
            }
        }

        private void UpdateDisplay(string text)
        {
            if (ModDisplay.Instance != null)
            {
                ModDisplay.Instance.Display(text);
            }
        }

        // ==== Словарь названий боссов в хоге, слева название самого моба, справа как его зовут в игре думаю это очевидно ====
        public static Dictionary<string, string> BossNames = new Dictionary<string, string>()
        {
            // Hall Of Gods
            { "Giant Fly", "Gruz Mother" },
            { "Giant Buzzer Col", "Vengerfly King" },
            { "Giant Buzzer Col (1)", "Vengerfly King" }, // дубликаты тоже можно обрабатывать
            { "Mawlek Body", "Brooding Mawlek" },
            { "False Knight New", "False Knight" },
            { "False Knight Dream", "Failed Champion" },
            { "Hornet Boss 1", "Hornet Protector" },
            { "Hornet Boss 2", "Hornet Sentinel" },
            { "Mega Moss Charger", "Massive Moss Charger" },
            { "Fluke Mother", "Flukemarm" },
            { "Mantis Lord", "Mantis Lord Phase 1" },
            { "Mantis Lord S1", "Mantis Lord Phase 2" },
            { "Mantis Lord S2", "Mantis Lord Phase 2" },
            { "Mantis Lord S3", "Sisters Of Battle" },
            { "Mega Fat Bee", "Oblobbles" },
            { "Mega Fat Bee (1)", "Oblobbles" },
            { "Hive Knight", "Hive Knight" },
            { "Infected Knight", "Broken Vessel" },
            { "Lost Kin", "Lost Kin" },
            { "Mimic Spider", "Nosk" },
            { "Hornet Nosk", "Winged Nosk" },
            { "Jar Collector", "The Collector" },
            { "Lancer", "God Tamer" },
            { "Lobster", "Beast" },
            { "Mega Zombie Beam Miner (1)", "Crystal Guardian" },
            { "Zombie Beam Miner Rematch", "Enraged Guardian" },
            { "Mega Jellyfish GG", "Uumu" },
            { "Mantis Traitor Lord", "Traitor Lord" },
            { "Grey Prince", "Grey Prince Zote" },
            { "Mage Knight", "Soul Warrior" },
            { "Mage Lord", "Soul Master" },
            { "Mage Lord Phase2", "Soul Master" },
            { "Dream Mage Lord", "Soul Tyrant" },
            { "Dream Mage Lord Phase2", "Soul Tyrant" },
            { "Dung Defender", "Dung Defender" },
            { "White Defender", "White Defender" },
            { "Black Knight 1", "Watcher Knight" },
            { "Black Knight 2", "Watcher Knight" },
            { "Black Knight 3", "Watcher Knight" },
            { "Black Knight 4", "Watcher Knight" },
            { "Black Knight 5", "Watcher Knight" },
            { "Black Knight 6", "Watcher Knight" },
            { "Ghost Warrior No Eyes", "No Eyes" },
            { "Ghost Warrior Marmu", "Marmu" },
            { "Ghost Warrior Xero", "Xero" },
            { "Ghost Warrior Markoth", "Markoth" },
            { "Ghost Warrior Galien", "Galien" },
            { "Ghost Warrior Slug", "Gorb" },
            { "Ghost Warrior Hu", "Elder Hu" },
            { "Mato", "Mato" },
            { "Oro", "Oro" },
            { "Sheo Boss", "Paintmaster Sheo" },
            { "Sly Boss", "Nailsage Sly" },
            { "HK Prime", "Pure Vessel" },
            { "Grimm Boss", "Grimm" },
            { "Nightmare Grimm Boss", "Nightmare King" },
            { "Absolute Radiance", "Radiance" },
        };
    }
}