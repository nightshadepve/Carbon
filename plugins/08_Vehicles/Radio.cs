using Facepunch;
using Network;
using Oxide.Core;
using ProtoBuf;
using System;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("Radio", "Karuza", "1.3.0")]
    public class Radio : RustPlugin
    {
        const string GLOBAL_PHONE = "Radio.GiveGlobalPhone";
        const string MOBILE_PHONE_ITEMNAME = "mobilephone";

        static Radio instance;

        Dictionary<ulong, IRadio> playerRadios = new Dictionary<ulong, IRadio>();
        Dictionary<ulong, PhoneController> playerPhones = new Dictionary<ulong, PhoneController>();

        Configuration configuration;

        bool hookSubscribed = false;

        void Unload()
        {
            DestroyRadioPhone();
            instance = null;
        }

        void OnServerInitialized()
        {
            Unsubscribe(nameof(OnPlayerVoice));

            instance = this;
            LoadConfig();
            RegisterCommands();
            RegisterPermissions();
            RegisterRadioPhone();
        }

        private void RegisterPermissions()
        {
            permission.RegisterPermission(GLOBAL_PHONE, this);
        }

        private void RegisterCommands()
        {
            cmd.AddChatCommand("GiveGlobalPhone", this, GiveGlobalPhone);
        }

        private void RegisterRadioPhone()
        {
            if (configuration.GlobalRadioPhoneNumber <= 0)
            {
                return;
            }

            var telephone = GameManager.server.CreateEntity("assets/bundled/prefabs/autospawn/phonebooth/phonebooth.static.prefab") as Telephone;
            telephone.Spawn();
            TelephoneManager.DeregisterTelephone(telephone.Controller);
            telephone.Controller.PhoneNumber = configuration.GlobalRadioPhoneNumber;
            telephone.Controller.PhoneName = configuration.GlobalRadioPhoneName;
            TelephoneManager.RegisterTelephone(telephone.Controller);
        }

        private void OnPlayerVoice(BasePlayer player, byte[] data)
        {
            IRadio playerRadio = null;
            if (!playerPhones.ContainsKey(player.net.ID.Value))
            {
                if (!playerRadios.ContainsKey(player.net.ID.Value))
                {
                    return;
                }

                playerRadio = playerRadios[player.net.ID.Value];
                if (!playerRadio.CanTransmitRadio())
                {
                    return;
                }
            }

            foreach (var radio in playerRadios.Values)
            {
                if (!radio.CanReceiveRadioCommunication(player, playerRadio))
                {
                    continue;
                }

                radio.ReceiveRadioCommunication(data);
            }

            foreach (var phone in playerPhones.Values)
            {
                if (phone.currentPlayer.net.ID.Value == player.net.ID.Value)
                {
                    continue;
                }

                //var target = RpcTarget.Player("OnReceivedVoice", phone.currentPlayer);
                var target = RpcTarget.SendInfo("OnReceivedVoice", new SendInfo(phone.currentPlayer.Connection)
                {
                    priority = Priority.Immediate
                });

                phone.ParentEntity.ClientRPC(target, data.Length, data);
            }
        }

        public static void RegisterRadio(BasePlayer player, IRadio radio)
        {
            if (player.IsNpc || player.IsBot)
            {
                return;
            }

            instance.playerRadios[player.net.ID.Value] = radio;

            if (!instance.hookSubscribed)
            {
                instance.Subscribe(nameof(OnPlayerVoice));
                instance.hookSubscribed = true;
            }
        }

        public static void RemoveRadio(BasePlayer player)
        {
            if (instance == null || player == null || player.IsNpc || player.IsBot)
            {
                return;
            }

            instance.playerRadios.Remove(player.net.ID.Value);

           if (instance.playerRadios.Count <= 0)
           {
                instance.Unsubscribe(nameof(OnPlayerVoice));
                instance.hookSubscribed = false;
            }
        }

        void DestroyRadioPhone()
        {
            var phone = TelephoneManager.GetTelephone(configuration.GlobalRadioPhoneNumber);
            if (phone == null)
            {
                return;
            }

            TelephoneManager.DeregisterTelephone(phone);
            if (phone.ParentEntity.IsDestroyed == false)
            {
                phone.ParentEntity.Kill();
            }
        }

        object OnPhoneDial(PhoneController callerPhone, PhoneController receiverPhone, BasePlayer player)
        {
            if (receiverPhone.PhoneNumber != configuration.GlobalRadioPhoneNumber)
            {
                return null;
            }

            callerPhone.ServerHangUp();
            callerPhone.SetPhoneStateWithPlayer(Telephone.CallState.InProcess);
            player.SetActiveTelephone(callerPhone);

            playerPhones.Add(player.net.ID.Value, callerPhone);
            return false;
        }

        object OnPhoneDialFailed(PhoneController phone, Telephone.DialFailReason reason, BasePlayer player)
        {
            if (player == null)
            {
                player = phone.currentPlayer;
            }

            if (player == null)
            {
                return null;
            }

            playerPhones.Remove(player.net.ID.Value);
            return null;
        }

        object OnPhoneDialFail(PhoneController phone, Telephone.DialFailReason reason, BasePlayer player)
        {
            if (player == null)
            {
                player = phone.currentPlayer;
            }

            if (player == null)
            {
                return null;
            }

            playerPhones.Remove(player.net.ID.Value);
            return null;
        }

        public static ItemDefinition FindItem(string itemNameOrId)
        {
            ItemDefinition itemDef = ItemManager.FindItemDefinition(itemNameOrId.ToLower());
            if (itemDef == null)
            {
                int itemId;
                if (int.TryParse(itemNameOrId, out itemId))
                {
                    itemDef = ItemManager.FindItemDefinition(itemId);
                }
            }

            return itemDef;
        }

        private void GiveGlobalPhone(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, GLOBAL_PHONE))
            {
                return;
            }

            Item item = ItemManager.Create(FindItem(MOBILE_PHONE_ITEMNAME));
            if (item == null)
            {
                return;
            }

            item.amount = 1;
            item.name = "Global Radio Phone";

            ItemContainer itemContainer = player.inventory.containerBelt;
            if (!player.inventory.GiveItem(item, itemContainer))
            {
                item.Remove();
                return;
            }

            var phone = item.GetHeldEntity() as MobilePhone;
            var controller = phone.Controller;
            if (controller.savedNumbers == null)
            {
                controller.savedNumbers = Pool.Get<PhoneDirectory>();
            }

            if (controller.savedNumbers.entries == null)
            {
                controller.savedNumbers.entries = Pool.Get<List<PhoneDirectory.DirectoryEntry>>();
            }

            if (controller.IsSavedContactValid(configuration.GlobalRadioPhoneName, configuration.GlobalRadioPhoneNumber))
            {
                var directoryEntry = Pool.Get<PhoneDirectory.DirectoryEntry>();
                directoryEntry.phoneName = configuration.GlobalRadioPhoneName;
                directoryEntry.phoneNumber = configuration.GlobalRadioPhoneNumber;
                directoryEntry.ShouldPool = false;
                controller.savedNumbers.entries.Add(directoryEntry);
            }

            controller.savedNumbers.ShouldPool = false;
            controller.baseEntity.SendNetworkUpdate();
        }

        public interface IRadio
        {
            bool CanTransmitRadio();
            bool CanReceiveRadioCommunication(BasePlayer player, IRadio transmittingRadio);
            void ReceiveRadioCommunication(byte[] data);
            int GetRadioFrequency();
        }

        public class Configuration
        {
            public int GlobalRadioPhoneNumber { get; set; } = 99994444;
            public string GlobalRadioPhoneName { get; set; } = "Global Radio";
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                configuration = Config.ReadObject<Configuration>();
                if (configuration == null)
                {
                    throw new Exception();
                }
            }
            catch
            {
                Config.WriteObject(configuration, false, $"{Interface.Oxide.ConfigDirectory}/{Name}.jsonError");
                PrintError("The configuration file contains an error and has been replaced with a default config.");
                LoadDefaultConfig();
            }

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            configuration = new Configuration();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configuration);
        }
    }
}
