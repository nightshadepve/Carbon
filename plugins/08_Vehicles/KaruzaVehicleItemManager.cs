using Oxide.Core;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("KaruzaVehicleItemManager", "Karuza", "1.0.54")]
    public class KaruzaVehicleItemManager : RustPlugin
    {
        const string BOX_SHORTNAME = "box.wooden.large";

        public static Dictionary<ulong, VehicleItemConfig> VehicleItemDict = new Dictionary<ulong, VehicleItemConfig>();

        Configuration configuration;
        List<VehicleItemConfig> defaultVehicleItems = new List<VehicleItemConfig>
        {
            // Planes
            new VehicleItemConfig { PrefabPath = "assets/custom/airbus.prefab", SkinID = 3304955504 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warbus.prefab", SkinID = 3304955700 },
            new VehicleItemConfig { PrefabPath = "assets/custom/fighterbus.prefab", SkinID = 3304632499 },
            new VehicleItemConfig { PrefabPath = "assets/custom/fighter.prefab", SkinID = 3304627495 },
            new VehicleItemConfig { PrefabPath = "assets/custom/skyplane.prefab", SkinID = 3304632844 },
            new VehicleItemConfig { PrefabPath = "assets/custom/skyboat.prefab", SkinID = 3304633054 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tinfighter.prefab", SkinID = 3602703654 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustwing.prefab", SkinID = 3304636255 },
            new VehicleItemConfig { PrefabPath = "assets/custom/marsfighter.prefab", SkinID = 3591754609 },
            new VehicleItemConfig { PrefabPath = "assets/custom/santa.prefab", SkinID = 3304955196 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warsanta.prefab", SkinID = 3304955338 },
            new VehicleItemConfig { PrefabPath = "assets/custom/witch.prefab", SkinID = 3304955096 },
            new VehicleItemConfig { PrefabPath = "assets/custom/magiccarpet.prefab", SkinID = 3304954964 },
            new VehicleItemConfig { PrefabPath = "assets/custom/heavyfighter.prefab", SkinID = 3320180998 },
            new VehicleItemConfig { PrefabPath = "assets/custom/wingfighter.prefab", SkinID = 3341945669 },
            new VehicleItemConfig { PrefabPath = "assets/custom/wingbomber.prefab", SkinID = 3342310318 },
            new VehicleItemConfig { PrefabPath = "assets/custom/timberwing.prefab", SkinID = 3341947091 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ufo_solo.prefab", SkinID = 3353713669 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ufo_duo.prefab", SkinID = 3353802572 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ufo_mothership.prefab", SkinID = 3353714203 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ufo_spooky.prefab", SkinID = 3359647681 },
            new VehicleItemConfig { PrefabPath = "assets/custom/driftwood.prefab", SkinID = 3359643345 },
            new VehicleItemConfig { PrefabPath = "assets/custom/raptor.prefab", SkinID = 3384380126 },
            new VehicleItemConfig { PrefabPath = "assets/custom/dronebackpack.prefab", SkinID = 3384380664 },
            new VehicleItemConfig { PrefabPath = "assets/custom/glider.prefab", SkinID = 3384380938 },
            new VehicleItemConfig { PrefabPath = "assets/custom/gliderarmed.prefab", SkinID = 3384381200 },
            new VehicleItemConfig { PrefabPath = "assets/custom/uap_solo.prefab", SkinID = 3384381497 },
            new VehicleItemConfig { PrefabPath = "assets/custom/uap_duo.prefab", SkinID = 3384381754 },
            new VehicleItemConfig { PrefabPath = "assets/custom/uap_prototype.prefab", SkinID = 3384382026 },
            new VehicleItemConfig { PrefabPath = "assets/custom/uap_xmas.prefab", SkinID = 3384382397 },
            new VehicleItemConfig { PrefabPath = "assets/custom/miniplane.prefab", SkinID = 3393719283 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warplane.prefab", SkinID = 3393719447 },
            new VehicleItemConfig { PrefabPath = "assets/custom/raidplane.prefab", SkinID = 3393719610 },
            new VehicleItemConfig { PrefabPath = "assets/custom/skywing.prefab", SkinID = 3397456143 },
            new VehicleItemConfig { PrefabPath = "assets/custom/starfighter.prefab", SkinID = 3400535847 },
            new VehicleItemConfig { PrefabPath = "assets/custom/oppressor.prefab", SkinID = 3429972346 },
            new VehicleItemConfig { PrefabPath = "assets/custom/imperialshuttle.prefab", SkinID = 3437930139 },
            new VehicleItemConfig { PrefabPath = "assets/custom/airspeeder.prefab", SkinID = 3437930578 },
            new VehicleItemConfig { PrefabPath = "assets/custom/f15solo.prefab", SkinID = 3439231615 },
            new VehicleItemConfig { PrefabPath = "assets/custom/f15duo.prefab", SkinID = 3439231743 },
            new VehicleItemConfig { PrefabPath = "assets/custom/a10.prefab", SkinID = 3810895234 },
            new VehicleItemConfig { PrefabPath = "assets/custom/batwing.prefab", SkinID = 3455850901 },
            new VehicleItemConfig { PrefabPath = "assets/custom/batwingduo.prefab", SkinID = 3455851072 },
            new VehicleItemConfig { PrefabPath = "assets/custom/flyingboat.prefab", SkinID = 3441601359 },
            new VehicleItemConfig { PrefabPath = "assets/custom/invader.prefab", SkinID = 3475792691 },
            new VehicleItemConfig { PrefabPath = "assets/custom/nighthawk.prefab", SkinID = 3512343436 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mininighthawk.prefab", SkinID = 3512343567 },
            new VehicleItemConfig { PrefabPath = "assets/custom/scrapjeta.prefab", SkinID = 3515081080 },
            new VehicleItemConfig { PrefabPath = "assets/custom/scrapjetb.prefab", SkinID = 3515081172 },
            new VehicleItemConfig { PrefabPath = "assets/custom/boogieboard.prefab", SkinID = 3517306571 },
            new VehicleItemConfig { PrefabPath = "assets/custom/leviathan.prefab", SkinID = 3539931146 },
            new VehicleItemConfig { PrefabPath = "assets/custom/leviathancarrier.prefab", SkinID = 3539931238 },
            new VehicleItemConfig { PrefabPath = "assets/custom/leviathangigacarrier.prefab", SkinID = 3539931354 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kamikaze.prefab", SkinID = 3543683753 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kamikaze_nuke.prefab", SkinID = 3714203841 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mig17.prefab", SkinID = 3599095555 },
            new VehicleItemConfig { PrefabPath = "assets/custom/harrier.prefab", SkinID = 3599095887 },
            new VehicleItemConfig { PrefabPath = "assets/custom/su47.prefab", SkinID = 3592114874 },
            new VehicleItemConfig { PrefabPath = "assets/custom/su47_qt.prefab", SkinID = 3592115081 },
            new VehicleItemConfig { PrefabPath = "assets/custom/su47_bling.prefab", SkinID = 3592115170 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cf105.prefab", SkinID = 3660174519 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tornado_grey.prefab", SkinID = 3600246115 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tornado_tan.prefab", SkinID = 3600246337 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetson.prefab", SkinID = 3538019780 },
            new VehicleItemConfig { PrefabPath = "assets/custom/viperwing.prefab", SkinID = 3636057961 },
            new VehicleItemConfig { PrefabPath = "assets/custom/nightwing.prefab", SkinID = 3636058137 },
            new VehicleItemConfig { PrefabPath = "assets/custom/razor.prefab", SkinID = 3567458728 },
            new VehicleItemConfig { PrefabPath = "assets/custom/stormwing.prefab", SkinID = 3697809667 },
            new VehicleItemConfig { PrefabPath = "assets/custom/c001a.prefab", SkinID = 3717703561 },
            new VehicleItemConfig { PrefabPath = "assets/custom/interceptor.prefab", SkinID = 3752723035 },
            new VehicleItemConfig { PrefabPath = "assets/custom/paraglider.prefab", SkinID = 3795165434 },
            new VehicleItemConfig { PrefabPath = "assets/custom/paragliderquad.prefab", SkinID = 3795165656 },
            new VehicleItemConfig { PrefabPath = "assets/custom/gunship.prefab", SkinID = 3795165827 },
            new VehicleItemConfig { PrefabPath = "assets/custom/deathwing_a.prefab", SkinID = 3806474269 },
            new VehicleItemConfig { PrefabPath = "assets/custom/deathwing_b.prefab", SkinID = 3806474272 },
            new VehicleItemConfig { PrefabPath = "assets/custom/deathwing_c.prefab", SkinID = 3807031648 },
            new VehicleItemConfig { PrefabPath = "assets/custom/toyplane.prefab", SkinID = 3806477692 },
            new VehicleItemConfig { PrefabPath = "assets/custom/toyjet.prefab", SkinID = 3806477799 },
            new VehicleItemConfig { PrefabPath = "assets/custom/toycargo.prefab", SkinID = 3806477911 },
            new VehicleItemConfig { PrefabPath = "assets/custom/toyrocket.prefab", SkinID = 3806478046 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_grey.prefab", SkinID = 3806258551 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_yellow.prefab", SkinID = 3806258424 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_white.prefab", SkinID = 3806258275 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_olive.prefab", SkinID = 3806258212 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_black.prefab", SkinID = 3806258094 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_wine.prefab", SkinID = 3806257880 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_pink.prefab", SkinID = 3806257778 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_blue.prefab", SkinID = 3806257604 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spark_purple.prefab", SkinID = 3806810178 },

            //Helicopter
            new VehicleItemConfig { PrefabPath = "assets/custom/littlebird.prefab", SkinID = 3591733142 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warbird.prefab", SkinID = 3591732692 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ah69t.prefab", SkinID = 3591492685 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ah69r.prefab", SkinID = 3591492822  },
            new VehicleItemConfig { PrefabPath = "assets/custom/ah69a.prefab", SkinID = 3591492741 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ah69g.prefab", SkinID = 3591492614 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mavik.prefab", SkinID = 3304635370 },
            new VehicleItemConfig { PrefabPath = "assets/custom/waterheli.prefab", SkinID = 3304631553 },
            new VehicleItemConfig { PrefabPath = "assets/custom/patrolheli.prefab", SkinID = 3304956227 },
            new VehicleItemConfig { PrefabPath = "assets/custom/patrolheli_seats.prefab", SkinID = 3726553202 },
            new VehicleItemConfig { PrefabPath = "assets/custom/porcelaincommander.prefab", SkinID = 3320177356 },
            //new VehicleItemConfig { PrefabPath = "assets/custom/cobra.prefab", SkinID = 3342315579 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_green.prefab", SkinID = 3591739914 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_grey.prefab", SkinID = 3591740064 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_black.prefab", SkinID = 3593394622 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_babyblue.prefab", SkinID = 3750412029 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_limegreen.prefab", SkinID = 3750412165 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_pink.prefab", SkinID = 3750412275 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_red.prefab", SkinID = 3750412434 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_orange.prefab", SkinID = 3750412540 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cobra_yellow.prefab", SkinID = 3750412646 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tardis.prefab", SkinID = 3359641974 },
            new VehicleItemConfig { PrefabPath = "assets/custom/talon.prefab", SkinID = 3384379565 },
            new VehicleItemConfig { PrefabPath = "assets/custom/helihatunarmed.prefab", SkinID = 3421776733 },
            new VehicleItemConfig { PrefabPath = "assets/custom/helihat.prefab", SkinID = 3421777224 },
            new VehicleItemConfig { PrefabPath = "assets/custom/falcon.prefab", SkinID = 3432359164 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mamba.prefab", SkinID = 3439032408 },
            new VehicleItemConfig { PrefabPath = "assets/custom/orlik.prefab", SkinID = 3486941098 },
            new VehicleItemConfig { PrefabPath = "assets/custom/oppressorcopter.prefab", SkinID = 3536950519 },
            new VehicleItemConfig { PrefabPath = "assets/custom/skycrane.prefab", SkinID = 3538069269 },
            new VehicleItemConfig { PrefabPath = "assets/custom/predator_x.prefab", SkinID = 3567674071 },
            new VehicleItemConfig { PrefabPath = "assets/custom/predator_y.prefab", SkinID = 3567674225 },
            new VehicleItemConfig { PrefabPath = "assets/custom/predator_rp.prefab", SkinID = 3567673954 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spookopter.prefab", SkinID = 3591305869 },
            new VehicleItemConfig { PrefabPath = "assets/custom/boltbucket.prefab", SkinID = 3607656691 },
            new VehicleItemConfig { PrefabPath = "assets/custom/crudebird.prefab", SkinID = 3607554924 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ah001.prefab", SkinID = 3614037875 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mh60x.prefab", SkinID = 3614162444 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetpack.prefab", SkinID = 3615513926 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetpackunarmed.prefab", SkinID = 3615514099 },
            new VehicleItemConfig { PrefabPath = "assets/custom/umbrellacopter.prefab", SkinID = 3636060264 },
            new VehicleItemConfig { PrefabPath = "assets/custom/deskcopter.prefab", SkinID = 3636060488 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spaceheli.prefab", SkinID = 3636057568 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spaceheliarmed.prefab", SkinID = 3636057795 },
            new VehicleItemConfig { PrefabPath = "assets/custom/assaultdrone.prefab", SkinID = 3636058367 },
            new VehicleItemConfig { PrefabPath = "assets/custom/apache.prefab", SkinID = 3679169539 },
            new VehicleItemConfig { PrefabPath = "assets/custom/policebird.prefab", SkinID = 3717364909 },
            new VehicleItemConfig { PrefabPath = "assets/custom/pookiecopter.prefab", SkinID = 3719410912 },
            new VehicleItemConfig { PrefabPath = "assets/custom/plushiecopter.prefab", SkinID = 3719411266 },
            new VehicleItemConfig { PrefabPath = "assets/custom/plushiecopter2.prefab", SkinID = 3721290469 },
            new VehicleItemConfig { PrefabPath = "assets/custom/plushiecopter3.prefab", SkinID = 3719411771 },
            new VehicleItemConfig { PrefabPath = "assets/custom/stingerblue.prefab", SkinID =  3752724484 },
            new VehicleItemConfig { PrefabPath = "assets/custom/stingerred.prefab", SkinID =  3754181835 },
            new VehicleItemConfig { PrefabPath = "assets/custom/helipark.prefab", SkinID = 3810286774 },

            // Car
            new VehicleItemConfig { PrefabPath = "assets/custom/atv.prefab", SkinID = 3304633269 },
            new VehicleItemConfig { PrefabPath = "assets/custom/atv_blue.prefab", SkinID = 3660191058 },
            new VehicleItemConfig { PrefabPath = "assets/custom/atv_camo.prefab", SkinID = 3660191201 },
            new VehicleItemConfig { PrefabPath = "assets/custom/atv_red.prefab", SkinID = 3660191301 },
            new VehicleItemConfig { PrefabPath = "assets/custom/atv_yellow.prefab", SkinID = 3660191380 },
            new VehicleItemConfig { PrefabPath = "assets/custom/racesofa.prefab", SkinID = 3304956030 },
            new VehicleItemConfig { PrefabPath = "assets/custom/trainwreck.prefab", SkinID = 3304634586 },
            new VehicleItemConfig { PrefabPath = "assets/custom/trainwrecker.prefab", SkinID = 3304634870 },
            new VehicleItemConfig { PrefabPath = "assets/custom/twistedtruck.prefab", SkinID = 3304956542 },
            new VehicleItemConfig { PrefabPath = "assets/custom/dunebuggie.prefab", SkinID = 3591754757 },
            new VehicleItemConfig { PrefabPath = "assets/custom/dunebuggieoffroad.prefab", SkinID = 3717364806 },
            new VehicleItemConfig { PrefabPath = "assets/custom/dunetruckarmed.prefab", SkinID = 3320173387 },
            new VehicleItemConfig { PrefabPath = "assets/custom/dunetruckunarmed.prefab", SkinID = 3320176268 },
            new VehicleItemConfig { PrefabPath = "assets/custom/doomsdaydiscovan.prefab", SkinID = 3320180260 },
            new VehicleItemConfig { PrefabPath = "assets/custom/forklift.prefab", SkinID = 3593397329 },
            //new VehicleItemConfig { PrefabPath = "assets/custom/lawnmower.prefab", SkinID = 3320219050 },
            new VehicleItemConfig { PrefabPath = "assets/custom/lawnmower_red.prefab", SkinID = 3591738202 },
            new VehicleItemConfig { PrefabPath = "assets/custom/lawnmower_green.prefab", SkinID = 3591737690 },
            new VehicleItemConfig { PrefabPath = "assets/custom/chariot.prefab", SkinID = 3320181563 },
            new VehicleItemConfig { PrefabPath = "assets/custom/soulharvester.prefab", SkinID = 3341443781 },
            new VehicleItemConfig { PrefabPath = "assets/custom/dreadnought.prefab", SkinID = 3341448356 },
            new VehicleItemConfig { PrefabPath = "assets/custom/dreadtrailer.prefab", SkinID = 3341447177 },
            new VehicleItemConfig { PrefabPath = "assets/custom/boatmobile.prefab", SkinID = 3341945311 },
            new VehicleItemConfig { PrefabPath = "assets/custom/onyx.prefab", SkinID = 3341945954 },
            new VehicleItemConfig { PrefabPath = "assets/custom/f1.prefab", SkinID = 3341946170 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlers.prefab", SkinID = 3341946584 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlerg.prefab", SkinID = 3341946793 },
            new VehicleItemConfig { PrefabPath = "assets/custom/landbeetle.prefab", SkinID = 3342309897 },
            new VehicleItemConfig { PrefabPath = "assets/custom/onyx_aa.prefab", SkinID = 3342310051 },
            new VehicleItemConfig { PrefabPath = "assets/custom/onyx_ifv.prefab", SkinID = 3342310158 },
            new VehicleItemConfig { PrefabPath = "assets/custom/wheelchair.prefab", SkinID = 3342316383 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tractor.prefab", SkinID = 3592434799 },
            new VehicleItemConfig { PrefabPath = "assets/custom/farmtractor.prefab", SkinID = 3599108191 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitruck_red.prefab", SkinID = 3342310427},
            new VehicleItemConfig { PrefabPath = "assets/custom/semitruck_white.prefab", SkinID = 3342311904 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitruck_green.prefab", SkinID = 3342313002 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitruck_yellow.prefab", SkinID = 3342314797 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitruck_blue.prefab", SkinID = 3342312310 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitrailer_orange.prefab", SkinID = 3342317066 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitrailer_blue.prefab", SkinID = 3342317473 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitrailer_green.prefab", SkinID = 3342317842 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitrailer_yellow.prefab", SkinID = 3342318300 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitrailer_fuel.prefab", SkinID = 3342318764 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mobilecasino.prefab", SkinID = 3342316147 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tricycle.prefab", SkinID = 3346486513 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shoppingcart.prefab", SkinID = 3346486704 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shoppingcart_blue.prefab", SkinID = 3346910705 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spookyshoppingcart.prefab", SkinID = 3346910998 },
            new VehicleItemConfig { PrefabPath = "assets/custom/batbike.prefab", SkinID = 3351506226 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spookybatbike.prefab", SkinID = 3351506940 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sportsbike_red.prefab", SkinID = 3351507331 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sportsbike_orange.prefab", SkinID = 3351507608 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sportsbike_blue.prefab", SkinID = 3351507886 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sportsbike_green.prefab", SkinID = 3351508300 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sportsbike_black.prefab", SkinID = 3351508593 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superbike_red.prefab", SkinID = 3544251162 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superbike_orange.prefab", SkinID = 3544250308 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superbike_blue.prefab", SkinID = 3544250857 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superbike_green.prefab", SkinID = 3544250117 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superbike_black.prefab", SkinID = 3544250603 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bikehellrider.prefab", SkinID = 3351509101 },
            new VehicleItemConfig { PrefabPath = "assets/custom/partytrailer.prefab", SkinID = 3351509582 },
            new VehicleItemConfig { PrefabPath = "assets/custom/partytrailer2.prefab", SkinID = 3695126650 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hoversled.prefab", SkinID = 3359643687 },
            new VehicleItemConfig { PrefabPath = "assets/custom/moonbuggy.prefab", SkinID = 3359643089 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mandospeeder.prefab", SkinID = 3359642758 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hailfirebike.prefab", SkinID = 3359650866 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warchair.prefab", SkinID = 3384379014 },
            new VehicleItemConfig { PrefabPath = "assets/custom/scooter.prefab", SkinID = 3384377815 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hoverscooter.prefab", SkinID = 3384378070 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hoverbatbike.prefab", SkinID = 3384378485 },
            new VehicleItemConfig { PrefabPath = "assets/custom/frostsled.prefab", SkinID = 3384377387 },
            new VehicleItemConfig { PrefabPath = "assets/custom/podspeeder.prefab", SkinID = 3384378716 },
            new VehicleItemConfig { PrefabPath = "assets/custom/heelies.prefab", SkinID = 3392724567 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ehoverboard.prefab", SkinID = 3392725075 },
            new VehicleItemConfig { PrefabPath = "assets/custom/monocycle.prefab", SkinID = 3392725407 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hovercraft.prefab", SkinID = 3392872830 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hovercraftarmed.prefab", SkinID = 3392873025 },
            new VehicleItemConfig { PrefabPath = "assets/custom/m939.prefab", SkinID = 3599083298 },
            new VehicleItemConfig { PrefabPath = "assets/custom/m939_enclosed.prefab", SkinID = 3599083449 },
            new VehicleItemConfig { PrefabPath = "assets/custom/m939_desert.prefab", SkinID = 3599083608 },
            new VehicleItemConfig { PrefabPath = "assets/custom/m939_desert_enclosed.prefab", SkinID = 3599083721 },
            new VehicleItemConfig { PrefabPath = "assets/custom/droneboard.prefab", SkinID = 3419871513 },
            new VehicleItemConfig { PrefabPath = "assets/custom/goblinglider.prefab", SkinID = 3421078110 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tumbler.prefab", SkinID = 3431103990 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tumblerarmed.prefab", SkinID = 3431163009 },
            new VehicleItemConfig { PrefabPath = "assets/custom/roadsterrp.prefab", SkinID = 3439032164 },
            new VehicleItemConfig { PrefabPath = "assets/custom/groundspeeder.prefab", SkinID = 3437931340 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ballistacar.prefab", SkinID = 3437934051 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ambulance.prefab", SkinID = 3439232220 },
            new VehicleItemConfig { PrefabPath = "assets/custom/landspeeder.prefab", SkinID = 3441553188 },
            new VehicleItemConfig { PrefabPath = "assets/custom/motortrike.prefab", SkinID = 3449936338 },
            new VehicleItemConfig { PrefabPath = "assets/custom/supertrike.prefab", SkinID = 3449936799 },
            new VehicleItemConfig { PrefabPath = "assets/custom/oppressorbike.prefab", SkinID = 3443061081 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sithspeeder.prefab", SkinID = 3456245630 },
            new VehicleItemConfig { PrefabPath = "assets/custom/carriage1.prefab", SkinID = 3463487527 },
            new VehicleItemConfig { PrefabPath = "assets/custom/carriage2.prefab", SkinID = 3463487813 },
            new VehicleItemConfig { PrefabPath = "assets/custom/carriage3.prefab", SkinID = 3463488142 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart1.prefab", SkinID = 3593315332 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart2.prefab", SkinID = 3593315540 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart3.prefab", SkinID = 3593315700 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart4.prefab", SkinID = 3593315875 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart5.prefab", SkinID = 3593316023 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart6.prefab", SkinID = 3593316132 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart7.prefab", SkinID = 3593316264 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart8.prefab", SkinID = 3593316382 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mongoose.prefab", SkinID = 3472373789 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warthog.prefab", SkinID = 3472374010 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warthogt.prefab", SkinID = 3472374190 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warthogs.prefab", SkinID = 3472374342 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ballcar.prefab", SkinID = 3472379405 },
            new VehicleItemConfig { PrefabPath = "assets/custom/slotscar.prefab", SkinID = 3472379595 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tablecar.prefab", SkinID = 3472379852 },
            new VehicleItemConfig { PrefabPath = "assets/custom/beancar.prefab", SkinID = 3472380009 },
            new VehicleItemConfig { PrefabPath = "assets/custom/drumcar.prefab", SkinID = 3472382725 },
            new VehicleItemConfig { PrefabPath = "assets/custom/pianocar.prefab", SkinID = 3472382977 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bonecar.prefab", SkinID = 3472383141 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bumperblue.prefab", SkinID = 3482062860 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bumperblack.prefab", SkinID = 3482062969 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bumperred.prefab", SkinID = 3482063050 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bumperorange.prefab", SkinID = 3482063130 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bumpergreen.prefab", SkinID = 3482063203 },
            new VehicleItemConfig { PrefabPath = "assets/custom/monstertruck.prefab", SkinID = 3593311806 },
            new VehicleItemConfig { PrefabPath = "assets/custom/monstertruck2.prefab", SkinID = 3593311999 },
            new VehicleItemConfig { PrefabPath = "assets/custom/monstertruckbat.prefab", SkinID = 3593312158 },
            new VehicleItemConfig { PrefabPath = "assets/custom/monstertruckbean.prefab", SkinID = 3593312353 },
            new VehicleItemConfig { PrefabPath = "assets/custom/monstertrucksemi.prefab", SkinID = 3593312527 },
            new VehicleItemConfig { PrefabPath = "assets/custom/policecar.prefab", SkinID = 3480197155 },
            new VehicleItemConfig { PrefabPath = "assets/custom/policebike.prefab", SkinID = 3480197341 },
            new VehicleItemConfig { PrefabPath = "assets/custom/swatvan.prefab", SkinID = 3480197446 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jeep.prefab", SkinID = 3486897889 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jeepjp.prefab", SkinID = 3486898040 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jeepcamo.prefab", SkinID = 3486898162 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jeepdesert.prefab", SkinID = 3486898288 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jeepaa.prefab", SkinID = 3486898412 },
            new VehicleItemConfig { PrefabPath = "assets/custom/flintmobile.prefab", SkinID = 3487419010 },
            new VehicleItemConfig { PrefabPath = "assets/custom/airboat.prefab", SkinID = 3487418748 },
            new VehicleItemConfig { PrefabPath = "assets/custom/airboat2.prefab", SkinID = 3487418880 },
            new VehicleItemConfig { PrefabPath = "assets/custom/schoolbus.prefab", SkinID = 3799342365 },
            new VehicleItemConfig { PrefabPath = "assets/custom/prisonbus.prefab", SkinID = 3799342558 },
            new VehicleItemConfig { PrefabPath = "assets/custom/citybus.prefab", SkinID = 3799342465 },
            new VehicleItemConfig { PrefabPath = "assets/custom/schoolbus1.prefab", SkinID = 3493431710 },
            new VehicleItemConfig { PrefabPath = "assets/custom/schoolbus2.prefab", SkinID = 3493431766 },
            new VehicleItemConfig { PrefabPath = "assets/custom/schoolbusshort.prefab", SkinID = 3493431818 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bigdumptruck.prefab", SkinID = 3493431872 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bigtractor.prefab", SkinID = 3493431941 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_intercettore.prefab", SkinID = 3599713753 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_strada_black.prefab", SkinID = 3600244831 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_strada_blue.prefab", SkinID = 3600245292 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_strada_gold.prefab", SkinID = 3600245204 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_strada_green.prefab", SkinID = 3600245095 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_strada_pink.prefab", SkinID = 3600245005 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_strada_yellow.prefab", SkinID = 3600244915 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_sport_red.prefab", SkinID = 3599605986 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_sport_blue.prefab", SkinID = 3599605888 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_sport_black.prefab", SkinID = 3599605765 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_sport_green.prefab", SkinID = 3599605637 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_sport_white.prefab", SkinID = 3599605534 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_sport_pink.prefab", SkinID = 3599605420 },
            new VehicleItemConfig { PrefabPath = "assets/custom/badcoppi_sport_yellow.prefab", SkinID = 3599605323 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minesweeper.prefab", SkinID = 3492018941 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minidozer.prefab", SkinID = 3492019000 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minitipper.prefab", SkinID = 3492019127 },
            new VehicleItemConfig { PrefabPath = "assets/custom/steamroller.prefab", SkinID = 3492019185 },
            new VehicleItemConfig { PrefabPath = "assets/custom/luggagecart.prefab", SkinID = 3506172978 },
            new VehicleItemConfig { PrefabPath = "assets/custom/luggagetrailer.prefab", SkinID = 3506173178 },
            new VehicleItemConfig { PrefabPath = "assets/custom/luggagetrailer2.prefab", SkinID = 3506173358 },
            new VehicleItemConfig { PrefabPath = "assets/custom/luggagetrailer3.prefab", SkinID = 3506173536 },
            new VehicleItemConfig { PrefabPath = "assets/custom/luggagetrailer4.prefab", SkinID = 3506173717 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hoverscout.prefab", SkinID = 3514995362 },
            new VehicleItemConfig { PrefabPath = "assets/custom/scoutspeeder.prefab", SkinID = 3536940684 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minibike.prefab", SkinID = 3529896282 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minitrike.prefab", SkinID = 3529896378 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eggmobile.prefab", SkinID = 3512345168 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eggkart.prefab", SkinID = 3512345300 },
            new VehicleItemConfig { PrefabPath = "assets/custom/drillcar.prefab", SkinID = 3512345434 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bm21.prefab", SkinID = 3567708048 },
            new VehicleItemConfig { PrefabPath = "assets/custom/towtruck.prefab", SkinID = 3568203736 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shifterkart1.prefab", SkinID = 3599924190 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shifterkart2.prefab", SkinID = 3599924261 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shifterkart3.prefab", SkinID = 3599924321 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shifterkart4.prefab", SkinID = 3599924404 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shifterkart5.prefab", SkinID = 3599924469 },
            new VehicleItemConfig { PrefabPath = "assets/custom/shifterkart6.prefab", SkinID = 3599924578 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tuktuk.prefab", SkinID = 3600448417 },
            new VehicleItemConfig { PrefabPath = "assets/custom/stretch_black.prefab", SkinID = 3607478184 },
            new VehicleItemConfig { PrefabPath = "assets/custom/stretch_white.prefab", SkinID = 3607478055 },
            new VehicleItemConfig { PrefabPath = "assets/custom/stretch_pink.prefab", SkinID = 3607477979 },
            new VehicleItemConfig { PrefabPath = "assets/custom/motorhome.prefab", SkinID = 3614037059 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cybertruck.prefab", SkinID = 3609417989 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cyberkart.prefab", SkinID = 3609418080 },
            new VehicleItemConfig { PrefabPath = "assets/custom/karttoy.prefab", SkinID = 3609418176 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartufo.prefab", SkinID = 3609418432 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartsemi.prefab", SkinID = 3609420186 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartsedan.prefab", SkinID = 3609420303 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartracebed.prefab", SkinID = 3610359235 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartpinata.prefab", SkinID = 3609420423 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartpie.prefab", SkinID = 3609420551 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartcoop.prefab", SkinID = 3609420655 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartcatapult.prefab", SkinID = 3609420772 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartcake.prefab", SkinID = 3609420891 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kartbradley.prefab", SkinID = 3609420998 },
            new VehicleItemConfig { PrefabPath = "assets/custom/scrapbuggy.prefab", SkinID = 3609417660 },
            new VehicleItemConfig { PrefabPath = "assets/custom/scrapcar.prefab", SkinID = 3609417744 },
            new VehicleItemConfig { PrefabPath = "assets/custom/scraptruck.prefab", SkinID = 3609417893 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_black.prefab", SkinID = 3617429031 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_blue.prefab", SkinID = 3617429150 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_gold.prefab", SkinID = 3617429243 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_green.prefab", SkinID = 3617429443 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_orange.prefab", SkinID = 3617429718 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_pink.prefab", SkinID = 3617429832 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_police.prefab", SkinID = 3617429977 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_purple.prefab", SkinID = 3617430102 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_red.prefab", SkinID = 3617430199 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_white.prefab", SkinID = 3617430278 },
            new VehicleItemConfig { PrefabPath = "assets/custom/diablo_yellow.prefab", SkinID = 3617430361 },
            new VehicleItemConfig { PrefabPath = "assets/custom/126p_blue.prefab", SkinID = 3617502042 },
            new VehicleItemConfig { PrefabPath = "assets/custom/126p_brown.prefab", SkinID = 3617501965 },
            new VehicleItemConfig { PrefabPath = "assets/custom/126p_purple.prefab", SkinID = 3617501881 },
            new VehicleItemConfig { PrefabPath = "assets/custom/126p_green.prefab", SkinID = 3617501820 },
            new VehicleItemConfig { PrefabPath = "assets/custom/126p_lime.prefab", SkinID = 3617501752 },
            new VehicleItemConfig { PrefabPath = "assets/custom/santasled.prefab", SkinID = 3618546323 },
            new VehicleItemConfig { PrefabPath = "assets/custom/portapotty.prefab", SkinID = 3636056790 },
            new VehicleItemConfig { PrefabPath = "assets/custom/spacebuggy.prefab", SkinID = 3636057371 },
            new VehicleItemConfig { PrefabPath = "assets/custom/chaircar.prefab", SkinID = 3636058552 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rockingchaircar.prefab", SkinID = 3636058720 },
            new VehicleItemConfig { PrefabPath = "assets/custom/beanbagcar.prefab", SkinID = 3636058889 },
            new VehicleItemConfig { PrefabPath = "assets/custom/beanbagcarduo.prefab", SkinID = 3636059064 },
            new VehicleItemConfig { PrefabPath = "assets/custom/beachchaircar.prefab", SkinID = 3636059302 },
            new VehicleItemConfig { PrefabPath = "assets/custom/beachchaircarduo.prefab", SkinID = 3636059477 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hoverthrone.prefab", SkinID = 3636059695 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rocketseat.prefab", SkinID = 3636059880 },
            new VehicleItemConfig { PrefabPath = "assets/custom/deskcar.prefab", SkinID = 3636060126 },
            new VehicleItemConfig { PrefabPath = "assets/custom/utilitytruck.prefab", SkinID = 3636060646 },
            new VehicleItemConfig { PrefabPath = "assets/custom/utilitytruck2.prefab", SkinID = 3636060788 },
            new VehicleItemConfig { PrefabPath = "assets/custom/utilitytruck3.prefab", SkinID = 3636060955 },
            new VehicleItemConfig { PrefabPath = "assets/custom/semitandemaxle.prefab", SkinID = 3636080042 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ares_hc.prefab", SkinID = 3652205166 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ares_aa.prefab", SkinID = 3652205067 },
            new VehicleItemConfig { PrefabPath = "assets/custom/ares_t.prefab", SkinID = 3652204964 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlux.prefab", SkinID = 3660148126 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlux_50cal.prefab", SkinID = 3660148041 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlux_armor.prefab", SkinID = 3660147961 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlux_he.prefab", SkinID = 3660147251 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlux_hv.prefab", SkinID = 3660147154 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlux_mlrs.prefab", SkinID = 3660147075 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rustlux_cargo.prefab", SkinID = 3668165142 },
            new VehicleItemConfig { PrefabPath = "assets/custom/fireambulance.prefab", SkinID = 3660171649 },
            new VehicleItemConfig { PrefabPath = "assets/custom/firetruck.prefab", SkinID = 3660172157 },
            new VehicleItemConfig { PrefabPath = "assets/custom/firelightrescue.prefab", SkinID = 3660171915 },
            new VehicleItemConfig { PrefabPath = "assets/custom/firepickuptruck.prefab", SkinID = 3660172033 },
            new VehicleItemConfig { PrefabPath = "assets/custom/hoverracer.prefab", SkinID = 3660173231 },
            new VehicleItemConfig { PrefabPath = "assets/custom/beevan.prefab", SkinID = 3660452958 },
            new VehicleItemConfig { PrefabPath = "assets/custom/foodtruck.prefab", SkinID = 3660453115 },
            new VehicleItemConfig { PrefabPath = "assets/custom/guntruck.prefab", SkinID = 3660453225 },
            new VehicleItemConfig { PrefabPath = "assets/custom/vaultvan.prefab", SkinID = 3660453348 },
            new VehicleItemConfig { PrefabPath = "assets/custom/deliveryvan.prefab", SkinID = 3660453442 },
            new VehicleItemConfig { PrefabPath = "assets/custom/stretch_charityspecial.prefab", SkinID = 3657618056 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cannoncar.prefab", SkinID =  3678802042 },
            new VehicleItemConfig { PrefabPath = "assets/custom/weedvan.prefab", SkinID =  3678802494 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warthogblack.prefab", SkinID =  3678802866 },
            new VehicleItemConfig { PrefabPath = "assets/custom/podracersky.prefab", SkinID =  3678803223 },
            new VehicleItemConfig { PrefabPath = "assets/custom/wedge.prefab", SkinID =  3679218825 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transit.prefab", SkinID =   3697807602 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitdropside.prefab", SkinID =   3697808041 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitrecycling.prefab", SkinID =   3697808456 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitbox.prefab", SkinID =   3697808693 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sentrycar.prefab", SkinID =   3697809021 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sentrycar2.prefab", SkinID =   3697809236 },
            new VehicleItemConfig { PrefabPath = "assets/custom/barrelcarthing.prefab", SkinID =   3697810981 },
            new VehicleItemConfig { PrefabPath = "assets/custom/humancar.prefab", SkinID =   3697811364 },
            new VehicleItemConfig { PrefabPath = "assets/custom/humankart.prefab", SkinID =   3697811695 },
            new VehicleItemConfig { PrefabPath = "assets/custom/monsterbike.prefab", SkinID =   3697812288 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tire.prefab", SkinID =   3697812775 },
            new VehicleItemConfig { PrefabPath = "assets/custom/zergatron.prefab", SkinID =   3697813122 },
            new VehicleItemConfig { PrefabPath = "assets/custom/oxideman.prefab", SkinID =   3697813618 },
            new VehicleItemConfig { PrefabPath = "assets/custom/coffincar.prefab", SkinID =   3697857763 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jackrabbit.prefab", SkinID = 3719407467 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jackrabbitgl.prefab", SkinID = 3719408103 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jackrabbitt.prefab", SkinID = 3719408996 },
            new VehicleItemConfig { PrefabPath = "assets/custom/megaboard.prefab", SkinID = 3719412325 },
            new VehicleItemConfig { PrefabPath = "assets/custom/longboard.prefab", SkinID = 3719412642 },
            new VehicleItemConfig { PrefabPath = "assets/custom/longboard2.prefab", SkinID = 3719412858 },
            new VehicleItemConfig { PrefabPath = "assets/custom/longboard3.prefab", SkinID = 3719413047 },
            new VehicleItemConfig { PrefabPath = "assets/custom/longboard4.prefab", SkinID =   3719413301 },
            new VehicleItemConfig { PrefabPath = "assets/custom/longboard5.prefab", SkinID =   3719413461 },
            new VehicleItemConfig { PrefabPath = "assets/custom/longboard6.prefab", SkinID =   3719413670 },
            new VehicleItemConfig { PrefabPath = "assets/custom/longboard7.prefab", SkinID = 3719413847 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mantis.prefab", SkinID = 3719414233 },
            new VehicleItemConfig { PrefabPath = "assets/custom/mantisduo.prefab", SkinID = 3719414425 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warthogtblack.prefab", SkinID = 3719414691 },
            new VehicleItemConfig { PrefabPath = "assets/custom/warthogsblack.prefab", SkinID = 3719414924 },
            new VehicleItemConfig { PrefabPath = "assets/custom/crane.prefab", SkinID = 3719412105 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart1offroad.prefab", SkinID = 3719416147 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart2offroad.prefab", SkinID = 3719416583 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart3offroad.prefab", SkinID = 3719416791 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart4offroad.prefab", SkinID = 3719416978 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart5offroad.prefab", SkinID = 3719417198 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart6offroad.prefab", SkinID = 3719417359 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart7offroad.prefab", SkinID = 3719417540 },
            new VehicleItemConfig { PrefabPath = "assets/custom/kart8offroad.prefab", SkinID = 3719417709 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_black.prefab", SkinID = 3738567633 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_blackwidow.prefab", SkinID = 3738567727 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_blue.prefab", SkinID = 3738567825 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_kpc1.prefab", SkinID = 3738567950 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_kpc2.prefab", SkinID = 3738568043 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_orange.prefab", SkinID = 3738568177 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_police.prefab", SkinID = 3738568300 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_purple.prefab", SkinID = 3738568434 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_red.prefab", SkinID = 3738568578 },
            new VehicleItemConfig { PrefabPath = "assets/custom/superglide_white.prefab", SkinID = 3738568656 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transit.prefab", SkinID =  3737678187 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitblack.prefab", SkinID =  3737678331 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitrecycling.prefab", SkinID =  3737678443 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitdropside.prefab", SkinID =  3737678539 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transittaxi.prefab", SkinID =  3737678539 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitbox.prefab", SkinID =  3737678722 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitnews.prefab", SkinID =  3791224313 },
            new VehicleItemConfig { PrefabPath = "assets/custom/factorycart.prefab", SkinID =  3737678920 },
            new VehicleItemConfig { PrefabPath = "assets/custom/motorwheel.prefab", SkinID =  3737679025 },
            new VehicleItemConfig { PrefabPath = "assets/custom/garbagetruck.prefab", SkinID =  3737679586 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eurotruck_black.prefab", SkinID =   3737451025 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eurotruck_white.prefab", SkinID =   3737451168 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eurotrailer_black.prefab", SkinID =   3737451281 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eurotrailer_green.prefab", SkinID =   3737451392 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eurotrailer_blue.prefab", SkinID =   3737451511 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eurotrailer_yellow.prefab", SkinID =   3737451708 },
            new VehicleItemConfig { PrefabPath = "assets/custom/eurotrailer_white.prefab", SkinID =   3737451806 },
            new VehicleItemConfig { PrefabPath = "assets/custom/utv.prefab", SkinID = 3719409868 },
            new VehicleItemConfig { PrefabPath = "assets/custom/utv6x4.prefab", SkinID = 3719410172 },
            new VehicleItemConfig { PrefabPath = "assets/custom/utvrecycler.prefab", SkinID = 3719410351 },
            new VehicleItemConfig { PrefabPath = "assets/custom/utvworkbench.prefab", SkinID = 3719410545 },
            new VehicleItemConfig { PrefabPath = "assets/custom/roadsweeper.prefab", SkinID =  3738795054 },
            new VehicleItemConfig { PrefabPath = "assets/custom/skipvan.prefab", SkinID =  3752207562 },
            new VehicleItemConfig { PrefabPath = "assets/custom/e30_black.prefab", SkinID =  3752721266 },
            new VehicleItemConfig { PrefabPath = "assets/custom/e30_drift.prefab", SkinID =  3752721527 },
            new VehicleItemConfig { PrefabPath = "assets/custom/e30_purple.prefab", SkinID =  3752722086 },
            new VehicleItemConfig { PrefabPath = "assets/custom/e30_red.prefab", SkinID =  3752721940 },
            new VehicleItemConfig { PrefabPath = "assets/custom/e30_white.prefab", SkinID =  3752722314 },
            new VehicleItemConfig { PrefabPath = "assets/custom/e30_silver.prefab", SkinID =  3752722629 },
            new VehicleItemConfig { PrefabPath = "assets/custom/rickshaw.prefab", SkinID = 3752722842 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_red.prefab", SkinID = 3750465740 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_yellow.prefab", SkinID = 3750465627 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_blue.prefab", SkinID = 3750465513 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_pink.prefab", SkinID = 3750465393 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_green.prefab", SkinID = 3750465263 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_purple.prefab", SkinID = 3750465130 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_orange .prefab", SkinID = 3750464986 },
            new VehicleItemConfig { PrefabPath = "assets/custom/retroracer_black.prefab", SkinID = 3750464901 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tubmarine.prefab", SkinID = 3766513892 },
            new VehicleItemConfig { PrefabPath = "assets/custom/golfbuggy1.prefab", SkinID = 3786994104 },
            new VehicleItemConfig { PrefabPath = "assets/custom/golfbuggy2.prefab", SkinID = 3786994136 },
            new VehicleItemConfig { PrefabPath = "assets/custom/golfbuggy3.prefab", SkinID = 3786994176 },
            new VehicleItemConfig { PrefabPath = "assets/custom/golfbuggy4.prefab", SkinID = 3786994228 },
            new VehicleItemConfig { PrefabPath = "assets/custom/golfbuggy5.prefab", SkinID = 3786994264 },
            new VehicleItemConfig { PrefabPath = "assets/custom/golfbuggy6.prefab", SkinID = 3786994308 },
            new VehicleItemConfig { PrefabPath = "assets/custom/golfbuggy7.prefab", SkinID = 3786994350 },
            new VehicleItemConfig { PrefabPath = "assets/custom/racesink.prefab", SkinID = 3795165994 },
            new VehicleItemConfig { PrefabPath = "assets/custom/racetoilet.prefab", SkinID = 3795166161 },
            new VehicleItemConfig { PrefabPath = "assets/custom/racetub.prefab", SkinID = 3795166299 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitelectric.prefab", SkinID = 3805191505 },
            new VehicleItemConfig { PrefabPath = "assets/custom/transitsecurity.prefab", SkinID = 3805191708 },
            new VehicleItemConfig { PrefabPath = "assets/custom/meattruck.prefab", SkinID = 3805191909 },
            new VehicleItemConfig { PrefabPath = "assets/custom/milkfloat.prefab", SkinID = 3805192274 },
            new VehicleItemConfig { PrefabPath = "assets/custom/racecornersofa.prefab", SkinID = 3809248460 },
            new VehicleItemConfig { PrefabPath = "assets/custom/axel.prefab", SkinID = 3809278827 },

            // Boats
            new VehicleItemConfig { PrefabPath = "assets/custom/sharksuit.prefab", SkinID =  3678803629 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sharksuitarmed.prefab", SkinID =  3678803996 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sharkboat.prefab", SkinID =  3678804247 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sharkboatarmed.prefab", SkinID =  3678804529 },
            new VehicleItemConfig { PrefabPath = "assets/custom/sharkboathuge.prefab", SkinID =  3678804788 },
            new VehicleItemConfig { PrefabPath = "assets/custom/standski.prefab", SkinID =  3678805116 },
            new VehicleItemConfig { PrefabPath = "assets/custom/standski2.prefab", SkinID =  3678805323 },
            new VehicleItemConfig { PrefabPath = "assets/custom/standski3.prefab", SkinID =  3678805781 },
            new VehicleItemConfig { PrefabPath = "assets/custom/standski4.prefab", SkinID =  3678806122 },
            new VehicleItemConfig { PrefabPath = "assets/custom/standski5.prefab", SkinID =  3678806281 },
            new VehicleItemConfig { PrefabPath = "assets/custom/standski6.prefab", SkinID =  3678806636 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskiblack.prefab", SkinID =  3679169928 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskiblue.prefab", SkinID =  3679170039 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskiblueredstriped.prefab", SkinID =  3679170179 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskiblueyellowstriped.prefab", SkinID =  3679170323 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskicamo.prefab", SkinID =  3679170472 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskigold.prefab", SkinID =  3679170603 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskigreen.prefab", SkinID =  3679170712 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskipink.prefab", SkinID =  3679170791 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskipolice.prefab", SkinID =  3679170890 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskipurple.prefab", SkinID =  3679170993 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskired.prefab", SkinID =  3679171101 },
            new VehicleItemConfig { PrefabPath = "assets/custom/jetskiyellow.prefab", SkinID =  3679171190 },
            new VehicleItemConfig { PrefabPath = "assets/custom/motorbikeboard.prefab", SkinID =  3679215956 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bikeboard.prefab", SkinID =  3679216206 },
            new VehicleItemConfig { PrefabPath = "assets/custom/trikeboard.prefab", SkinID =  3679216472 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minirhib.prefab", SkinID =  3697885595 },
            new VehicleItemConfig { PrefabPath = "assets/custom/miniptboat.prefab", SkinID =  3697886456 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minisub.prefab", SkinID =  3697886692 },
            new VehicleItemConfig { PrefabPath = "assets/custom/minisub2.prefab", SkinID =  3697886948 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cpv_grey.prefab", SkinID =  3697696931 },
            new VehicleItemConfig { PrefabPath = "assets/custom/cpv_green.prefab", SkinID =  3697697057 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bananaboat.prefab", SkinID = 3721290603 },
            new VehicleItemConfig { PrefabPath = "assets/custom/bananaboathuge.prefab", SkinID = 3721290774 },
            new VehicleItemConfig { PrefabPath = "assets/custom/galleon.prefab", SkinID = 3733884735 },
            new VehicleItemConfig { PrefabPath = "assets/custom/tubmarine.prefab", SkinID = 3766513892 },
            
            // Bradley
            new VehicleItemConfig { PrefabPath = "assets/custom/bradleyvehicle.prefab", SkinID = 3416082800 },
        };

        #region ServerHooks

        void Unload()
        {
            VehicleItemDict.Clear();
            VehicleItemDict = null;
            configuration = null;
        }
        private void Init()
        {
            Unsubscribe(nameof(OnEntityBuilt));
        }

        void OnServerInitialized()
        {
            LoadConfig();

            if (configuration.SubscribeToOnEntityBuilt)
            {
                Subscribe(nameof(OnEntityBuilt));
            }
        }

        #endregion

        #region ChatCommands

        [ChatCommand("GiveVehicleItem")]
        private void GiveVehicleItem(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                return;
            }

            if (args.Length <= 0)
            {
                return;
            }

            var vehicleType = args[0];
            var vehiclePath = $"assets/custom/{vehicleType}.prefab";
            foreach (var vehicleConfig in VehicleItemDict.Values)
            {
                if (!vehiclePath.Equals(vehicleConfig.PrefabPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                var item = ItemManager.CreateByPartialName(BOX_SHORTNAME, 1, vehicleConfig.SkinID);
                item.name = vehicleType;
                player.GiveItem(item);

                return;
            }

            player.ChatMessage($"Invalid Vehicle");
        }

        [ChatCommand("GiveAllVehicleItems")]
        private void GiveAllVehicleItems(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                return;
            }

            foreach (var vehicleConfig in VehicleItemDict.Values)
            {
                var item = ItemManager.CreateByPartialName(BOX_SHORTNAME, 1, vehicleConfig.SkinID);
                item.name = vehicleConfig.PrefabPath.Replace("assets/custom/", "").Replace(".prefab", "");
                player.GiveItem(item);
            }
        }

        #endregion

        #region Hooks

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (plan == null)
            {
                return;
            }

            var item = plan.GetItem();
            if (object.ReferenceEquals(item, null))
            {
                return;
            }

            if (item.skin <= 0)
            {
                return;
            }

            if (!VehicleItemDict.ContainsKey(item.skin))
            {
                return;
            }

            var vi = VehicleItemDict[item.skin];

            var player = plan.GetOwnerPlayer();
            if (object.ReferenceEquals(player, null))
            {
                return;
            }

            var pos = go.transform.position + (Vector3.up * 2f) + (player.eyes.HeadForward() * 2);
            var rot = go.transform.rotation;
            var vehicle = GameManager.server.CreateEntity(vi.PrefabPath, pos, rot);
            if (vehicle == null)
            {
                Debug.LogError($"{vi.PrefabPath} failed to spawn - Check the prefab path");
                return;
            }

            vehicle.skinID = item.skin;
            vehicle.OwnerID = player.userID;
            vehicle.Spawn();

            go.ToBaseEntity().Kill();
        }

        #endregion

        #region Config

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                bool updateConfig = false;
                configuration = Config.ReadObject<Configuration>();
                if (configuration == null)
                {
                    LoadDefaultConfig();
                    updateConfig = true;
                }


                for (int i = 0; i < defaultVehicleItems.Count; i++)
                {
                    var defaultItem = defaultVehicleItems[i];
                    var foundItems = configuration.VehicleItems.FindAll(vi => vi.SkinID == defaultItem.SkinID);
                    if (foundItems.Count > 0)
                    {
                        var updateItem = false;
                        for (int j = foundItems.Count - 1; j >= 0; j--)
                        {
                            var foundItem = foundItems[j];
                            if (defaultItem.PrefabPath.Contains("tinfighter") && foundItem.PrefabPath.Contains("tiefighter"))
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("rustwing") && foundItem.PrefabPath.Contains("xwing"))
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("timberwing") && foundItem.PrefabPath.Contains("woodplane"))
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("onyx_aa") && foundItem.PrefabPath.Contains("onyxaa"))
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("onyx_ifv") && foundItem.PrefabPath.Contains("onyxifv"))
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("hailfirebike") && foundItem.PrefabPath.Contains("wheeli"))
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("jeepjp") && defaultItem.SkinID == 3486897889)
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("cf105") && defaultItem.SkinID == 3600226043)
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else if (defaultItem.PrefabPath.Contains("a10") && defaultItem.SkinID == 3599095387)
                            {
                                updateItem = true;
                                configuration.VehicleItems.Remove(foundItem);
                            }
                            else
                            {
                                continue;
                            }
                        }

                        if (updateItem)
                        {
                            updateConfig = true;
                            if (!configuration.VehicleItems.Exists(vi => vi.SkinID == defaultItem.SkinID))
                            {
                                configuration.VehicleItems.Add(new VehicleItemConfig()
                                {
                                    SkinID = defaultItem.SkinID,
                                    PrefabPath = defaultItem.PrefabPath,
                                });
                            }
                        }
                    }
                    else
                    {
                        updateConfig = true;
                        configuration.VehicleItems.Add(new VehicleItemConfig()
                        {
                            SkinID = defaultItem.SkinID,
                            PrefabPath = defaultItem.PrefabPath,
                        });
                    }
                }

                if (updateConfig)
                {
                    SaveConfig();
                }

                VehicleItemDict = new Dictionary<ulong, VehicleItemConfig>();
                for (int i = 0; i < configuration.VehicleItems.Count; i++)
                {
                    var vi = configuration.VehicleItems[i];
                    if (VehicleItemDict.ContainsKey(vi.SkinID))
                    {
                        var existingItem = VehicleItemDict[vi.SkinID];
                        continue;
                    }

                    VehicleItemDict[vi.SkinID] = vi;
                }
            }
            catch (Exception ex)
            {
                Config.WriteObject(configuration, false, $"{Interface.Oxide.ConfigDirectory}/{Name}.jsonError");
                PrintError($"The configuration file contains an error. {ex}");
            }
        }

        protected override void LoadDefaultConfig()
        {
            configuration = new Configuration
            {
                VehicleItems = defaultVehicleItems
            };
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configuration);
        }

        public class Configuration
        {
            public bool SubscribeToOnEntityBuilt = true;
            public List<VehicleItemConfig> VehicleItems = new List<VehicleItemConfig>();
        }

        public class VehicleItemConfig
        {
            public string PrefabPath { get; set; }
            public ulong SkinID { get; set; }
        }

        #endregion
    }
}
