namespace Acsp.Data;

public enum Region { Asia, Europe, NorthAmerica, SouthAmerica, MiddleEast, Africa, Oceania }

public sealed record AirportInfo(string Code, string Name, double Lat, double Lon, Region Region);

/// <summary>
/// Curated database of real airports (approximate coordinates) used by the synthetic
/// instance generator to mimic the networks of the four airline archetypes (§9.1).
/// </summary>
public static class AirportDb
{
    public static readonly IReadOnlyList<AirportInfo> All = Parse("""
        HKG|Hong Kong|22.31|113.91|Asia
        TPE|Taipei|25.08|121.23|Asia
        KHH|Kaohsiung|22.58|120.35|Asia
        MNL|Manila|14.51|121.02|Asia
        CEB|Cebu|10.31|123.98|Asia
        SIN|Singapore|1.36|103.99|Asia
        BKK|Bangkok|13.69|100.75|Asia
        HKT|Phuket|8.11|98.31|Asia
        KUL|Kuala Lumpur|2.75|101.71|Asia
        PEN|Penang|5.30|100.28|Asia
        CGK|Jakarta|-6.13|106.66|Asia
        SUB|Surabaya|-7.38|112.79|Asia
        DPS|Denpasar|-8.75|115.17|Asia
        SGN|Ho Chi Minh City|10.82|106.65|Asia
        HAN|Hanoi|21.22|105.81|Asia
        PNH|Phnom Penh|11.55|104.85|Asia
        RGN|Yangon|16.91|96.13|Asia
        DAC|Dhaka|23.84|90.40|Asia
        CCU|Kolkata|22.65|88.45|Asia
        MAA|Chennai|12.98|80.16|Asia
        BLR|Bengaluru|13.20|77.71|Asia
        HYD|Hyderabad|17.24|78.43|Asia
        BOM|Mumbai|19.09|72.87|Asia
        DEL|Delhi|28.57|77.10|Asia
        CMB|Colombo|7.17|79.88|Asia
        KHI|Karachi|24.91|67.16|Asia
        LHE|Lahore|31.52|74.40|Asia
        ICN|Seoul Incheon|37.46|126.44|Asia
        PUS|Busan|35.18|128.94|Asia
        NRT|Tokyo Narita|35.76|140.39|Asia
        KIX|Osaka Kansai|34.43|135.24|Asia
        NGO|Nagoya|34.86|136.81|Asia
        FUK|Fukuoka|33.59|130.45|Asia
        CTS|Sapporo|42.78|141.69|Asia
        PVG|Shanghai Pudong|31.14|121.81|Asia
        PEK|Beijing|40.08|116.58|Asia
        CAN|Guangzhou|23.39|113.30|Asia
        SZX|Shenzhen|22.64|113.81|Asia
        XMN|Xiamen|24.54|118.13|Asia
        TAO|Qingdao|36.27|120.37|Asia
        CTU|Chengdu|30.57|103.95|Asia
        CKG|Chongqing|29.72|106.64|Asia
        WUH|Wuhan|30.78|114.21|Asia
        TSN|Tianjin|39.12|117.35|Asia
        DLC|Dalian|38.97|121.54|Asia
        HGH|Hangzhou|30.23|120.43|Asia
        NKG|Nanjing|31.74|118.86|Asia
        MFM|Macau|22.15|113.59|Asia
        BWN|Bandar Seri Begawan|4.94|114.93|Asia
        KTM|Kathmandu|27.70|85.36|Asia
        ALA|Almaty|43.35|77.04|Asia
        TAS|Tashkent|41.26|69.28|Asia
        ULN|Ulaanbaatar|47.84|106.77|Asia
        LUX|Luxembourg|49.63|6.20|Europe
        FRA|Frankfurt|50.03|8.57|Europe
        HHN|Frankfurt Hahn|49.95|7.26|Europe
        CDG|Paris CDG|49.01|2.55|Europe
        ORY|Paris Orly|48.72|2.38|Europe
        AMS|Amsterdam|52.31|4.76|Europe
        BRU|Brussels|50.90|4.48|Europe
        LGG|Liege|50.64|5.44|Europe
        OST|Ostend|51.20|2.87|Europe
        LHR|London Heathrow|51.47|-0.45|Europe
        STN|London Stansted|51.89|0.26|Europe
        EMA|East Midlands|52.83|-1.33|Europe
        MAN|Manchester|53.35|-2.28|Europe
        DUB|Dublin|53.43|-6.25|Europe
        EDI|Edinburgh|55.95|-3.37|Europe
        MXP|Milan Malpensa|45.63|8.72|Europe
        BGY|Bergamo|45.67|9.70|Europe
        FCO|Rome Fiumicino|41.80|12.25|Europe
        VCE|Venice|45.50|12.35|Europe
        BLQ|Bologna|44.53|11.29|Europe
        TRN|Turin|45.20|7.65|Europe
        NAP|Naples|40.89|14.29|Europe
        MAD|Madrid|40.47|-3.56|Europe
        BCN|Barcelona|41.30|2.08|Europe
        VLC|Valencia|39.49|-0.48|Europe
        ZAZ|Zaragoza|41.67|-1.04|Europe
        VIT|Vitoria|42.88|-2.72|Europe
        SVQ|Seville|37.42|-5.89|Europe
        LIS|Lisbon|38.77|-9.13|Europe
        OPO|Porto|41.24|-8.68|Europe
        VIE|Vienna|48.11|16.57|Europe
        ZRH|Zurich|47.46|8.55|Europe
        GVA|Geneva|46.24|6.11|Europe
        BSL|Basel|47.59|7.53|Europe
        MUC|Munich|48.35|11.79|Europe
        BER|Berlin|52.36|13.50|Europe
        HAM|Hamburg|53.63|9.99|Europe
        CGN|Cologne|50.87|7.14|Europe
        DUS|Dusseldorf|51.29|6.77|Europe
        STR|Stuttgart|48.69|9.19|Europe
        NUE|Nuremberg|49.50|11.08|Europe
        LEJ|Leipzig|51.42|12.24|Europe
        PRG|Prague|50.10|14.26|Europe
        BTS|Bratislava|48.17|17.21|Europe
        WAW|Warsaw|52.17|20.97|Europe
        KRK|Krakow|50.08|19.78|Europe
        GDN|Gdansk|54.38|18.47|Europe
        BUD|Budapest|47.44|19.26|Europe
        OTP|Bucharest|44.57|26.10|Europe
        SOF|Sofia|42.70|23.40|Europe
        BEG|Belgrade|44.82|20.31|Europe
        ZAG|Zagreb|45.74|16.07|Europe
        LJU|Ljubljana|46.22|14.46|Europe
        ATH|Athens|37.94|23.94|Europe
        SKG|Thessaloniki|40.52|22.97|Europe
        IST|Istanbul|41.26|28.74|Europe
        ESB|Ankara|40.13|32.99|Europe
        CPH|Copenhagen|55.62|12.65|Europe
        BLL|Billund|55.74|9.15|Europe
        ARN|Stockholm|59.65|17.92|Europe
        GOT|Gothenburg|57.66|12.28|Europe
        OSL|Oslo|60.19|11.10|Europe
        HEL|Helsinki|60.32|24.96|Europe
        TLL|Tallinn|59.41|24.83|Europe
        RIX|Riga|56.92|23.97|Europe
        VNO|Vilnius|54.63|25.29|Europe
        KBP|Kyiv|50.34|30.89|Europe
        SVO|Moscow SVO|55.97|37.41|Europe
        LED|St Petersburg|59.80|30.27|Europe
        LYS|Lyon|45.73|5.08|Europe
        MRS|Marseille|43.44|5.22|Europe
        TLS|Toulouse|43.63|1.36|Europe
        NCE|Nice|43.66|7.22|Europe
        LIL|Lille|50.56|3.09|Europe
        JFK|New York JFK|40.64|-73.78|NorthAmerica
        EWR|Newark|40.69|-74.17|NorthAmerica
        BOS|Boston|42.36|-71.01|NorthAmerica
        PHL|Philadelphia|39.87|-75.24|NorthAmerica
        IAD|Washington Dulles|38.94|-77.46|NorthAmerica
        ORD|Chicago ORD|41.98|-87.90|NorthAmerica
        DTW|Detroit|42.21|-83.35|NorthAmerica
        MSP|Minneapolis|44.88|-93.22|NorthAmerica
        STL|St Louis|38.75|-90.37|NorthAmerica
        MEM|Memphis|35.04|-89.98|NorthAmerica
        SDF|Louisville|38.17|-85.74|NorthAmerica
        CVG|Cincinnati|39.05|-84.66|NorthAmerica
        CLT|Charlotte|35.21|-80.94|NorthAmerica
        ATL|Atlanta|33.64|-84.43|NorthAmerica
        MIA|Miami|25.79|-80.29|NorthAmerica
        MCO|Orlando|28.43|-81.31|NorthAmerica
        TPA|Tampa|27.98|-82.53|NorthAmerica
        IAH|Houston|29.98|-95.34|NorthAmerica
        DFW|Dallas|32.90|-97.04|NorthAmerica
        DEN|Denver|39.86|-104.67|NorthAmerica
        PHX|Phoenix|33.43|-112.01|NorthAmerica
        SLC|Salt Lake City|40.79|-111.98|NorthAmerica
        LAX|Los Angeles|33.94|-118.41|NorthAmerica
        SAN|San Diego|32.73|-117.19|NorthAmerica
        SFO|San Francisco|37.62|-122.38|NorthAmerica
        OAK|Oakland|37.72|-122.22|NorthAmerica
        PDX|Portland|45.59|-122.60|NorthAmerica
        SEA|Seattle|47.45|-122.31|NorthAmerica
        ANC|Anchorage|61.17|-149.98|NorthAmerica
        HNL|Honolulu|21.32|-157.92|NorthAmerica
        YYZ|Toronto|43.68|-79.63|NorthAmerica
        YUL|Montreal|45.47|-73.74|NorthAmerica
        YVR|Vancouver|49.19|-123.18|NorthAmerica
        YYC|Calgary|51.11|-114.02|NorthAmerica
        MEX|Mexico City|19.44|-99.07|NorthAmerica
        GDL|Guadalajara|20.52|-103.31|NorthAmerica
        MTY|Monterrey|25.78|-100.11|NorthAmerica
        SJU|San Juan|18.44|-66.00|NorthAmerica
        SDQ|Santo Domingo|18.43|-69.67|NorthAmerica
        HAV|Havana|22.99|-82.41|NorthAmerica
        KIN|Kingston|17.94|-76.79|NorthAmerica
        PTY|Panama City|9.07|-79.38|SouthAmerica
        SJO|San Jose CR|9.99|-84.20|SouthAmerica
        SAL|San Salvador|13.44|-89.06|SouthAmerica
        GUA|Guatemala City|14.58|-90.53|SouthAmerica
        BOG|Bogota|4.70|-74.14|SouthAmerica
        MDE|Medellin|6.16|-75.42|SouthAmerica
        UIO|Quito|-0.11|-78.35|SouthAmerica
        GYE|Guayaquil|-2.16|-79.88|SouthAmerica
        LIM|Lima|-12.02|-77.11|SouthAmerica
        LPB|La Paz|-16.51|-68.19|SouthAmerica
        SCL|Santiago|-33.39|-70.79|SouthAmerica
        EZE|Buenos Aires|-34.82|-58.53|SouthAmerica
        MVD|Montevideo|-34.84|-56.03|SouthAmerica
        ASU|Asuncion|-25.24|-57.52|SouthAmerica
        GRU|Sao Paulo GRU|-23.43|-46.47|SouthAmerica
        VCP|Campinas|-23.00|-47.13|SouthAmerica
        GIG|Rio de Janeiro|-22.81|-43.25|SouthAmerica
        CWB|Curitiba|-25.53|-49.18|SouthAmerica
        POA|Porto Alegre|-29.99|-51.17|SouthAmerica
        REC|Recife|-8.13|-34.92|SouthAmerica
        MAO|Manaus|-3.04|-60.05|SouthAmerica
        CCS|Caracas|10.60|-66.99|SouthAmerica
        DXB|Dubai|25.25|55.36|MiddleEast
        AUH|Abu Dhabi|24.43|54.65|MiddleEast
        SHJ|Sharjah|25.33|55.52|MiddleEast
        DOH|Doha|25.27|51.61|MiddleEast
        BAH|Bahrain|26.27|50.63|MiddleEast
        KWI|Kuwait City|29.24|47.97|MiddleEast
        RUH|Riyadh|24.96|46.70|MiddleEast
        JED|Jeddah|21.68|39.16|MiddleEast
        DMM|Dammam|26.47|49.80|MiddleEast
        MCT|Muscat|23.59|58.28|MiddleEast
        AMM|Amman|31.72|35.99|MiddleEast
        BEY|Beirut|33.82|35.49|MiddleEast
        TLV|Tel Aviv|32.01|34.89|MiddleEast
        BGW|Baghdad|33.26|44.23|MiddleEast
        IKA|Tehran|35.42|51.15|MiddleEast
        CAI|Cairo|30.12|31.40|Africa
        CMN|Casablanca|33.37|-7.59|Africa
        ALG|Algiers|36.69|3.22|Africa
        TUN|Tunis|36.85|10.23|Africa
        NBO|Nairobi|-1.32|36.93|Africa
        ADD|Addis Ababa|8.98|38.80|Africa
        EBB|Entebbe|0.04|32.44|Africa
        DAR|Dar es Salaam|-6.88|39.20|Africa
        KRT|Khartoum|15.59|32.55|Africa
        JNB|Johannesburg|-26.14|28.25|Africa
        CPT|Cape Town|-33.97|18.60|Africa
        LOS|Lagos|6.58|3.32|Africa
        ACC|Accra|5.61|-0.17|Africa
        ABJ|Abidjan|5.26|-3.93|Africa
        DKR|Dakar|14.74|-17.49|Africa
        LAD|Luanda|-8.86|13.23|Africa
        FIH|Kinshasa|-4.39|15.44|Africa
        SYD|Sydney|-33.95|151.18|Oceania
        MEL|Melbourne|-37.67|144.84|Oceania
        BNE|Brisbane|-27.38|153.12|Oceania
        PER|Perth|-31.94|115.97|Oceania
        AKL|Auckland|-37.01|174.79|Oceania
        CHC|Christchurch|-43.49|172.53|Oceania
        NAN|Nadi|-17.76|177.44|Oceania
        POM|Port Moresby|-9.44|147.22|Oceania
        GUM|Guam|13.48|144.80|Oceania
        """);

    public static AirportInfo Get(string code) => _byCode[code];
    private static readonly Dictionary<string, AirportInfo> _byCode = All.ToDictionary(a => a.Code);

    private static List<AirportInfo> Parse(string raw) =>
        raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(line => line.Split('|'))
           .Select(p => new AirportInfo(p[0], p[1],
               double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
               double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
               Enum.Parse<Region>(p[4])))
           .ToList();
}

public static class GreatCircle
{
    /// <summary>Great-circle distance in km between two airports (haversine).</summary>
    public static double Km(AirportInfo a, AirportInfo b) => Km(a.Lat, a.Lon, b.Lat, b.Lon);

    public static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = Rad(lat2 - lat1), dLon = Rad(lon2 - lon1);
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;
}
