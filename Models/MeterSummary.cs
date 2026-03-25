namespace RIoT2.Net.Devices.Models
{
    public enum ApCode
    {
        Success = 0,
        DataException = 1000,
        NoData = 1001,
        ApplicationAccountException = 2000,
        InvalidApplicationAccount = 2001,
        ApplicationAccountNotAuthorized = 2002,
        ApplicationAccountAuthorizationExpires = 2003,
        ApplicationAccountNoPermission = 2004,
        ApplicationAccountAccessLimitExceeded = 2005,
        AccessTokenException = 3000,
        MissingAccessToken = 3001,
        UnableToVerifyAccessToken = 3002,
        AccessTokenTimeout = 3003,
        RefreshTokenTimeout = 3004,
        RequestParameterException = 4000,
        InvalidRequestParameter = 4001,
        InternalServerException = 5000,
        CommunicationException = 6000,
        ServerAccessRestrictionException = 7000,
        ServerAccessLimitExceeded = 7001,
        TooManyRequests = 7002,
        SystemBusy = 7003
    }

    public class Data
    {
        public EnergyMeter Month { get; set; }
        public EnergyMeter Year { get; set; }
        public EnergyMeter Today { get; set; }
        public EnergyMeter Lifetime { get; set; }
    }

    public class EnergyMeter
    {
        public string Consumed { get; set; }
        public string Exported { get; set; }
        public string Imported { get; set; }
        public string Produced { get; set; }
    }

    public class MeterSummary
    {
        public ApCode Code { get; set; }
        public Data Data { get; set; }
    }
}
