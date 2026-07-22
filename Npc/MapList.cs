using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lycoris.Npc
{
    /// <summary>A selectable map: its id (folder name, e.g. "t101i01") and a friendly label.</summary>
    public sealed class MapEntry
    {
        public string Id { get; }
        public string Name { get; }
        public MapEntry(string id, string name) { Id = id; Name = name; }
        public string Label => string.IsNullOrEmpty(Name) || Name == Id ? Id : $"{Id} — {Name}";
        public override string ToString() => Label;
    }

    /// <summary>YW3 map id → in-game location name, and discovery of the maps present in an extract.</summary>
    public static class MapList
    {
        public static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"t001g00","New Yo-kai City"}, {"t101g00","Uptown Springdale"}, {"t102g00","Mt. Wildwood"},
            {"t103g00","Blossom Heights"}, {"t104g00","Downtown Springdale"}, {"t105g00","Shopper's Row"},
            {"t106g00","Breezy Hills"}, {"t107g00","Excellent Tower"}, {"t108g00","Sparkopolis"},
            {"t109g00","Greenfields Station"}, {"t121g00","San Fantastico"}, {"t131g00","Harrisville"},
            {"t132g00","Harrisville Station Plaza"}, {"t201g00","Old Springdale"}, {"t202g00","Old Mt. Wildwood"},
            {"t206g00","Gourd Pond"}, {"t231g00","Old Harrisville"}, {"t232g00","Old Harrisville Station Plaza"},
            {"t401g00","Southmond"}, {"t402g00","Northbeech"}, {"t403g00","East Pine"}, {"t404g00","Hazeltine Mansion"},
            {"t405g00","Secret Forest Base"}, {"t406g00","Meadowbrooke Farm"}, {"t411g00","Dukesville"},
            {"t412g00","Pecan Port"}, {"t501g00","Cluvian Continent"}, {"t100i21","Mirror of Truth"},
            {"t101i01","Adams House - 1F"}, {"t101i02","Adams House - 2F"}, {"t101i03","Forester House - 1F"},
            {"t101i04","Forester House - 2F"}, {"t101i10","Logan's House"}, {"t101i21","Banter Bakery"},
            {"t101i23","Everymart Uptown"}, {"t101i25","Springdale Community Center"}, {"t101i27","Piggleston Bank"},
            {"t101i29","Lambert Post Office"}, {"t101i31","Jungle Hunter"}, {"t101i51","Springdale Elementary - 1F South"},
            {"t101i52","Springdale Elementary - 1F North"}, {"t101i53","Springdale Elementary - 2F"},
            {"t101i55","Springdale Elementary - 3F"}, {"t101i58","Springdale Elementary - Roof"},
            {"t101i59","Springdale Elementary Gym"}, {"t102i01","Deserted House"}, {"t102i21","Shrine Behind the Waterfall"},
            {"t103i01","Bernstein House - 1F"}, {"t103i02","Bernstein House - 2F"}, {"t103i03","Bernstein House - 3F"},
            {"t103i10","Megan's House"}, {"t103i21","Timers & More"}, {"t103i23","Candy Stop"},
            {"t103i25","Everymart Blossom Heights"}, {"t103i31","Shoten Temple"}, {"t103i33","Prayer's Peak Tunnel"},
            {"t103i35","Chloro-Phil Good"}, {"t103i37","Springdale Hot Springs - Lobby"}, {"t103i38","Springdale Hot Springs - Men"},
            {"t103i39","Springdale Hot Springs - Women"}, {"t103i51","Byrd House - Mayan Room"}, {"t103i53","Byrd House - Hidden Room"},
            {"t103i60","Wayfarer Manor - Room 101"}, {"t103i61","Wayfarer Manor - Room 102"}, {"t103i62","Wayfarer Manor - Room 103"},
            {"t103i63","Wayfarer Manor - Room 104"}, {"t103i64","Wayfarer Manor - Room 105"}, {"t103i65","Wayfarer Manor - Room 201"},
            {"t103i66","Wayfarer Manor - Room 202"}, {"t103i67","Wayfarer Manor - Room 203"}, {"t103i68","Wayfarer Manor - Room 204"},
            {"t103i69","Wayfarer Manor - VIP Room"}, {"t104i10","Springdale Underground Area"}, {"t104i21","Seabreeze Tunnel"},
            {"t104i23","Frostia's Place"}, {"t104i25","Arcadia Arcade"}, {"t104i27","Nom Burger"},
            {"t104i29","Fortune Hospital - 1F"}, {"t104i30","Fortune Hospital - 2F"}, {"t104i31","Foundation Academy"},
            {"t104i33","Everymart Downtown"}, {"t104i35","Café Shanista"}, {"t104i37","Springdale Sports Club - 1F"},
            {"t104i38","Springdale Sports Club - 2F"}, {"t104i39","Springdale Sports Club - 3F"}, {"t104i41","Belly Buster Curry House"},
            {"t104i43","Tortoise Camera Shop"}, {"t104i45","Sunset Mall - B1"}, {"t104i47","Sunset Mall - 1F"},
            {"t104i49","Sunset Mall - Dolphin Cafe"}, {"t104i51","Springdale Business Tower - 1F"}, {"t104i53","Springdale Business Tower - 7F"},
            {"t104i55","Sumptuous Sukiyaki"}, {"t104i61","Springdale Central Station"}, {"t105i20","Springdale Flower Road"},
            {"t105i21","Settle In Bookstore"}, {"t105i23","North Wind Ramen"}, {"t105i25","Everymart Shopper's Row"},
            {"t105i27","Sun Pavilion"}, {"t105i29","Toys iZ We"}, {"t105i31","Mary's Coin Laundry"},
            {"t105i33","Superior Style"}, {"t105i35","Whatta Find"}, {"t105i37","Sushi Springdale"}, {"t105i39","Tempura Tempest"},
            {"t106i01","Archer House - 1F"}, {"t106i02","Archer House - 2F"}, {"t106i05","Stone House"},
            {"t106i07","Amy's House - 1F"}, {"t106i09","Amy's House - 2F"}, {"t106i10","Lina's House"}, {"t106i11","Thomas House"},
            {"t106i13","Breezy Hills Apartments - Entrance"}, {"t106i15","Breezy Hills Apartments - 7F"}, {"t106i21","Everymart Breezy Hills"},
            {"t106i23","Trophy Room"}, {"t106i51","Wisteria Gardens Parking"}, {"t106i52","Wisteria Gardens Entrance"},
            {"t106i55","Stonewood House"}, {"t107i01","Excellent Tower - Entrance"}, {"t107i02","Excellent Tower - Elevator"},
            {"t107i03","Excellent Tower - Observation Deck"}, {"t108i01","Next HarMEOWny Theater - Entrance"},
            {"t108i02","Next HarMEOWny Theater - Stage"}, {"t108i05","AnimeChum"}, {"t108i07","Maid in Heaven"},
            {"t108i09","Everymart Sparkopolis"}, {"t108i11","Detective Agency"}, {"t108i13","Soba Sensation"},
            {"t121i01","Rolling Waves Meeting Hall"}, {"t121i03","Rusty's Mart"}, {"t121i10","Deserted House"}, {"t121i20","Sea"},
            {"t131i01","Grandma's House"}, {"t131i03","Harrisville School"}, {"t131i05","Mountain Market"},
            {"t401i01","BBQ - Adams House - 1F"}, {"t401i02","BBQ - Adams House - 2F"}, {"t401i03","Suspicious Room"},
            {"t401i07","BBQ - Forester House - 1F"}, {"t401i08","BBQ - Forester House - 2F"}, {"t401i11","Margarita's"},
            {"t401i13","House of Boxes"}, {"t401i21","Acornia Bank"}, {"t401i23","Skycutter Post Office"},
            {"t401i51","Southmond School"}, {"t401i52","Southmond School - West Building"}, {"t401i53","Southmond School - East Building"},
            {"t401i55","Southmond School - Cafeteria"}, {"t402i01","Everymart Northbeech"}, {"t402i03","Springdale Trading - St. Peanutsburg"},
            {"t402i05","Bon Voyage Boats"}, {"t402i07","Miracle Circus"}, {"t402i09","Star Burger"}, {"t402i11","Tinker's Toy Town"},
            {"t402i13","Sunrise Sushi"}, {"t402i15","Amore Pizza"}, {"t402i21","Warehouse No. 3 Marketplace"},
            {"t402i31","City Hall - 1F"}, {"t402i32","City Hall - 2F"}, {"t402i33","City Hall - Rooftop"},
            {"t403i01","East Pine Church"}, {"t403i03","Bob's Watches"}, {"t403i05","Steak and Pine 29"}, {"t403i07","Old House"},
            {"t403i13","Tempura Temptations"}, {"t403i15","Succulent Sukiyaki"}, {"t403i60","Wayfarer Manor - Room 101"},
            {"t403i61","Wayfarer Manor - Room 102"}, {"t403i62","Wayfarer Manor - Room 103"}, {"t403i63","Wayfarer Manor - Room 104"},
            {"t403i64","Wayfarer Manor - Room 105"}, {"t403i65","Wayfarer Manor - Room 201"}, {"t403i66","Wayfarer Manor - Room 202"},
            {"t403i67","Wayfarer Manor - Room 203"}, {"t403i68","Wayfarer Manor - Room 204"}, {"t403i69","Wayfarer Manor - Room 205"},
            {"t405i01","Secret Base"}, {"t411i01","Outlaw Hotel"}, {"t411i03","Sheriff's Office"}, {"t411i05","Wild Hunters"},
            {"t001i51","Club Koma"}, {"t001i53","Enma Villa"}, {"t001i55","Yopple Store"}, {"t001i57","Phoenix Sushi"},
            {"t001i59","Serenity Tempura"}, {"t001i60","Blasters House - B1"}, {"t001i61","Blasters House"}, {"t001i71","Haven Sukiyaki"},
            {"t501i01","Cluvian Continent - Blasters Camp"}, {"t001d41","Ambrosia Pavilion - Entrance"},
            {"t001d42","Yo-kai World - Entry Hall"}, {"t001d46","Ambrosia Pavilion"}, {"t001d49","Treasure Ship of the Seven Gods"},
            {"t001d21","Yopple HQ - Garden"}, {"t001d23","Yopple HQ - Lobby"}, {"t001d25","Yopple HQ - CEO's Office"},
            {"t001d27","Yopple HQ - Yo-kai Watch Center"}, {"t001d29","Yopple HQ - Meeting Room"}, {"t001d31","Yopple HQ - Lab"},
            {"t001d33","Yopple HQ - Assembly Line"}, {"t001d35","Yopple HQ - Fighting Room"}, {"t001d37","Yopple HQ - Cafeteria"},
            {"t001d39","Yopple HQ - The Fish Tank"}, {"t001d51","Bada-Bing Tower - Lobby"}, {"t001d53","Bada-Bing Tower - Elevator"},
            {"t001d55","Dangerous Floor"}, {"t001d57","Dream Floor - Moon"}, {"t001d59","Dream Floor - Fire"},
            {"t001d61","Dream Floor - Water"}, {"t001d63","Dream Floor - Woods"}, {"t001d64","Dream Floor - Woods"},
            {"t001d65","Dream Floor - Gold"}, {"t001d66","Dream Floor - Gold"}, {"t001d67","Dream Floor - Earth"},
            {"t001d69","Dream Floor - Sun"}, {"t001d71","Ghoulfather's Office"}, {"t001d73","UFO Interior"},
            {"t100d01","Springdale Sewers - Mouse Alley"}, {"t100d02","Springdale Sewers - Secret Space"},
            {"t100d03","Springdale Sewers - Cat Square"}, {"t100d04","Springdale Sewers - Neon Town"},
            {"t100d05","Springdale Sewers - Main Street"}, {"t100d06","Springdale Sewers - School Route"},
            {"t100d07","Springdale Sewers - Kaos March Mural"}, {"t100d08","Springdale Sewers - Bracing Path"},
            {"t100d09","Springdale Sewers - Breezy Hills Byway"}, {"t100d10","Springdale Sewers - Frogetmenot's Inn"},
            {"t100d11","Sparkopolis Sewers - Meeting Place"}, {"t100d12","Springdale Sewers - Yo-kai Parade Mural"},
            {"t100d13","Springdale Sewers - Downtown Westside"}, {"t101d01","Shady Back Alley"}, {"t101d02","Lonely Waterway"},
            {"t101d03","The Catwalk"}, {"t101d05","Desolate Lane"}, {"t102d01","Mt. Wildwood - Mountain Path"},
            {"t102d02","Mt. Wildwood - Summit"}, {"t102d03","Jumbo Slider"}, {"t102d31","Abandoned Tunnel West"},
            {"t102d32","Abandoned Tunnel East"}, {"t103d01","Tucked Away Lot"}, {"t103d03","Hidden Side Street"},
            {"t103d11","Secret Byway"}, {"t103d31","Old Mansion - Main House"}, {"t103d33","Old Mansion - Side House"},
            {"t103d35","Old Mansion - Main House Attic"}, {"t103d36","Old Mansion - Side House Attic"},
            {"t104d01","Academy Shortcut"}, {"t104d03","Behind Frostia's Place"}, {"t104d05","Delivery Bay"},
            {"t104d07","Springdale Underground Parking"}, {"t104d11","Springdale Business Tower - 4F"},
            {"t104d13","Springdale Business Tower - 13F"}, {"t104d15","Springdale Business Tower - East Stairs"},
            {"t104d17","Springdale Business Tower - West Stairs"}, {"t104d21","Springdale Business Tower - CEO's Off."},
            {"t105d01","Shopping Street Narrows"}, {"t105d11","Tranquility Apts. - A-102"}, {"t105d12","Tranquility Apts. - A-104"},
            {"t105d13","Tranquility Apts. - A-201"}, {"t105d14","Tranquility Apts. - A-303"}, {"t105d15","Tranquility Apts. - B-202"},
            {"t105d16","Tranquility Apts. - B-204"}, {"t105d17","Tranquility Apts. - B-301"}, {"t105d18","Tranquility Apts. - B-302"},
            {"t105d19","Tranquility Apts. - C-102"}, {"t105d20","Tranquility Apts. - B-102"}, {"t105d21","Tranquility Apts. - B-101"},
            {"t105d22","Tranquility Apts. - C-104"}, {"t105d23","Tranquility Apts. - A-202"}, {"t105d24","Tranquility Apts. - A-204"},
            {"t105d25","Tranquility Apts. - A-103"}, {"t105d26","Tranquility Apts. - C-201"}, {"t105d27","Tranquility Apts. - A-302"},
            {"t105d28","Tranquility Apts. - B-303"}, {"t105d29","Tranquility Apts. - C-203"}, {"t105d30","Tranquility Apts. - C-204"},
            {"t105d31","Tranquility Apts. - C-301"}, {"t105d32","Tranquility Apts. - C-302"}, {"t105d33","Tranquility Apts. - B-301 (Past)"},
            {"t105d34","Tranquility Apts. - B-302 (Past)"}, {"t105d35","Tranquility Apts. - B-303 (Past)"},
            {"t105d36","Tranquility Apts. - B-304 (Past)"}, {"t105d41","Nocturne Hospital - 1F"}, {"t105d43","Nocturne Hospital - 2F"},
            {"t105d45","Nocturne Hospital - 3F"}, {"t105d47","Hospital - Basement"}, {"t105d48","Hospital - Basement Lab"},
            {"t106d11","Rugged Path"}, {"t106d31","Gourd Pond Museum - 1F"}, {"t106d32","Gourd Pond Museum - 2F"},
            {"t106d33","Gourd Pond Museum - Vault"}, {"t108d01","Shady Parking Lot"}, {"t108d03","Junk Alley"},
            {"t109d11","Hazy Manor"}, {"t109d13","Hazy Manor"}, {"t109d15","Lotus Lake"}, {"t109d17","Cape of Evening Calm"},
            {"t109d19","Forevermore Falls"}, {"t109d21","Classroom"}, {"t109d23","Blind Alley"}, {"t109d25","Requiem Riverside"},
            {"t121d01","Briny Grotto"}, {"t121d03","Hidden Workshop"}, {"t121d11","Seaside Cave"}, {"t121d13","Dragon Heights"},
            {"t131d01","Rice Paddy Path"}, {"t131d03","Fullface Rock"}, {"t131d04","Cicada Canyon"}, {"t131d05","Mt. Middleton Summit"},
            {"t132d01","Alley off the Plaza"}, {"t400d01","River Tootin Tributary"}, {"t400d11","Remote Relic"},
            {"t400d13","Grumbler's Grotto - Entrance"}, {"t400d15","Solitary Sanctuary"}, {"t400d17","Forest Islet"},
            {"t400d19","Secret Cavern"}, {"t401d01","Garden Grill"}, {"t401d03","Denton's Repairs"}, {"t401d21","Scrapyard"},
            {"t402d01","Hip & Hopping Alley"}, {"t402d03","Warehouse No. 2"}, {"t402d21","Pearly Chamber of Whimsy"},
            {"t402d26","Whimsical Shopping - 1st Room"}, {"t402d27","Whimsical Shopping - 2nd Room"}, {"t402d28","Whimsical Shopping - 3rd Room"},
            {"t402d31","Whimsical Work - 1st Room"}, {"t402d32","Whimsical Work - 2nd Room"}, {"t402d33","Whimsical Work - 3rd Room"},
            {"t402d36","Whimsical Quiz - 1st Room"}, {"t402d37","Whimsical Quiz - 2nd Room"}, {"t402d38","Whimsical Quiz - 3rd Room"},
            {"t402d39","Whimsical Quiz - Punishment Room"}, {"t402d41","Whimsical Drilling - 1st Room"}, {"t402d42","Whimsical Drilling - 2nd Room"},
            {"t402d43","Whimsical Drilling - 3rd Room"}, {"t402d44","Whimsical Drilling - 4th Room"}, {"t403d01","Hotel - Rear Byway"},
            {"t403d11","Phantomart"}, {"t403d13","Phantomart - Storeroom"}, {"t404d01","Hazeltine Mansion - Undergr. Passage"},
            {"t404d03","Hazeltine Mansion - Entrance Hall"}, {"t404d05","Hazeltine Mansion - 1F Hallway"},
            {"t404d07","Hazeltine Mansion - Dining Room"}, {"t404d09","Hazeltine Mansion - Judgement Hallway"},
            {"t404d11","Hazeltine Mansion - Kitchen"}, {"t404d13","Hazeltine Mansion - Bathroom"}, {"t404d15","Hazeltine Mansion - 2F Hallway"},
            {"t404d17","Hazeltine Mansion - Archive"}, {"t404d19","Hazeltine Mansion"}, {"t405d11","Gloombell Forest"},
            {"t405d13","Mushroom Park"}, {"t405d21","Grumbler's Grotto"}, {"t405d23","Grumbler's Grotto"},
            {"t406d21","Meadowbrooke Farm"}, {"t411d00","Hopper's Gorge"}, {"t411d10","Hopper's Gorge - Condor Canyon"},
            {"t411d21","Tower of Zenlightenment - 1st Trial"}, {"t411d22","Tower of Zenlightenment - 2nd Trial"},
            {"t411d23","Tower of Zenlightenment - 3rd Trial"}, {"t411d24","Tower of Zenlightenment - 4th Trial"},
            {"t411d25","Tower of Zenlightenment - 5th Trial"}, {"t411d26","Tower of Zenlightenment - 6th Trial"},
            {"t411d27","Tower of Zenlightenment - 7th Trial"}, {"t411d28","Tower of Zenlightenment - 8th Trial"},
            {"t411d29","Tower of Zenlightenment - 9th Trial"}, {"t411d30","Tower of Zenlightenment - Final Trial"},
            {"t411d41","Ascension Ground"}, {"t501d01","Banquet Room"}, {"t581d01","Cluvian Keyhole - Entrance"},
            {"t581d02","Tomb of King Clupharaon - Entr."}, {"t581d03","Temple of Clusis - Entrance"},
            {"t581d04","Statue of Clunubis - Entrance"}, {"t581d05","Pyramid of Clu - Entrance"}, {"t581d06","Mt. Cluvimpus - Entrance"},
            {"t581d07","Cluphinx - Entrance"}, {"t581d08","Tower of Clu - Entrance"}, {"t581d09","Enma Palace - Entrance"},
            {"t581d12","Sky Garden - Entrance"}, {"t490d01","Suspicious Room"}, {"t592o10","Enma Palace"},
            {"t100s01","Springdale Central Station"}, {"t100s02","Green St Station (Sparkopolis)"}, {"t100s03","Hibarly Hills Station"},
            {"t100s04","Petal Peak Station"}, {"t100s05","Factory Row Station"}, {"t100s06","Sweet Meadow Station"},
            {"t100s07","Fortune Place Station"}, {"t100s08","Skybridge Station"}, {"t100s09","Dreamer's Field Station"},
            {"t100s10","Ridgemont Station"}, {"t100s11","Bayside Station"}, {"t100s12","San Fantastico Station"},
            {"t100s13","Greenfields Station"}, {"t100s14","Temple Park Station"}, {"t100s15","Dingle Falls Station"},
            {"t100s16","Harrisville Station"}, {"t100s17","Spring Station"}, {"t100s18","Sunshine Station"},
            {"t100s19","Little Haven Station"}, {"t100s20","Scarfit Downs Station"}, {"t100s21","Cherry Hill Station"},
            {"t100s22","Whimsy Valley Station"},
        };

        /// <summary>Look up a friendly name (empty string if unknown).</summary>
        public static string NameOf(string id) => id != null && Names.TryGetValue(id, out var n) ? n : "";

        /// <summary>
        /// Discover the maps present under the given roots (a map = a res/map/&lt;id&gt; folder containing npc.pck),
        /// labelled with their known name. Sorted by id. Only roots that exist are scanned.
        /// </summary>
        public static List<MapEntry> Available(params string[] roots)
        {
            var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
            {
                foreach (var mapRoot in new[] { Path.Combine(root, "res", "map"), Path.Combine(root, "data", "res", "map"), root })
                {
                    if (!Directory.Exists(mapRoot)) continue;
                    try
                    {
                        foreach (var dir in Directory.EnumerateDirectories(mapRoot))
                            if (File.Exists(Path.Combine(dir, "npc.pck"))) ids.Add(Path.GetFileName(dir));
                    }
                    catch { /* ignore unreadable roots */ }
                }
            }
            return ids.Select(id => new MapEntry(id, NameOf(id))).ToList();
        }
    }
}
