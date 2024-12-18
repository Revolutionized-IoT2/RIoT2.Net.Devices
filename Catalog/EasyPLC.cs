using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core;


namespace RIoT2.Net.Devices.Catalog
{
    internal class EasyPLC : DeviceBase, IRefreshableReportDevice, ICommandDevice
    {
        private TcpClient _client;
        private string _plcIp;
        private int _plcPort = 10001;
        private bool _connected;
        private Dictionary<string, bool> _states = new Dictionary<string, bool>();
        private readonly object plcLock = new object();
        private readonly object connectLock = new object();

        public EasyPLC(ILogger logger) : base(logger) 
        {
            _connected = false;
        } 

        public void ExecuteCommand(string commandId, string value)
        {
            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            if (command.Address == "refresh") 
            {
                readMarkers();
                //readMarkers(3); //TODO make better system to refresh net id 3 
                return;
            }

            var name = command.Address.Split('-');
            if (name.Length != 4)
                return;

            int netId = int.Parse(name[1]);
            int expansion = int.Parse(name[2]);
            int marker = int.Parse(name[3]);

            bool valueBool = false;

            if (bool.TryParse(value, out valueBool)) 
            {
                writeMarker(marker, valueBool, netId, expansion);
            }
        }

        public void Start()
        {
            if (String.IsNullOrEmpty(_plcIp)) 
            {
                Logger.LogWarning("Could not start PLC Connection. IP address not defined.");
                return;
            }
            //connect().Wait();
        }

        public override void ConfigureDevice()
        {
            _plcIp = GetConfiguration<string>("ipAddress");
            _plcPort = GetConfiguration<int>("port");
        }

        public void Stop()
        {
            disconnect();
        }

        public override void Refresh(ReportTemplate report)
        {
            if (report != null) //report should be null because in this case, refresh is defined on device level
                return;

            //this will read markers from PLC and Send report if anyone with template has changed
            readMarkers();
            //readMarkers(3); //TODO Cu
        }

        #region Easy PLC methods 

        private async Task connect() 
        {
            try
            {
                if (_client == null)
                    _client = new TcpClient();

                if (!_client.Connected)
                {
                    _connected = false;
                    byte[] testConnection = new byte[] { 0x45, 0x07, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x5f }; //test PLC?
                    byte[] initConnection = new byte[] { 0x45, 0x07, 0x01, 0x00, 0x80, 0x00, 0x00, 0x3d, 0x9f }; //init baud rate?
                    _client.Connect(_plcIp, _plcPort);

                    var tcpStream = _client.GetStream();
                    byte[] receiveBufferTest = new byte[8];
                    byte[] receiveBufferInit = new byte[8];

                    await tcpStream.WriteAsync(testConnection, 0, testConnection.Length);
                    await tcpStream.ReadAsync(receiveBufferTest, 0, receiveBufferTest.Length);

                    await tcpStream.WriteAsync(initConnection, 0, initConnection.Length);
                    await tcpStream.ReadAsync(receiveBufferInit, 0, receiveBufferInit.Length);

                    if (okResponse(receiveBufferTest) && okResponse(receiveBufferInit))
                    {
                        Logger.LogInformation("Connected to Easy PLC");
                        _connected = true;
                    }
                    else
                    {
                        Logger.LogWarning("Could not connect to Easy PLC");
                    }
                }
            }
            catch (Exception x) 
            {
                Logger.LogError(x, "Error connecting Easy PLC");
                _connected = false;
            }
        }
        private void disconnect() 
        {
            if (_client != null) 
            {
                if (_client.Connected)
                    _client.Close();

                _client.Dispose();
            }
            _connected = false;
        }
        private byte[] sendAndReceive(byte[] message)
        {
            lock (plcLock) //ensure that only one operation is performed at a time
            {
                if (!_connected)
                    connect().Wait();

                try
                {

                    var tcpStream = _client.GetStream();

                    byte[] receiveBuffer = new byte[256];

                    var writeTask = tcpStream.WriteAsync(message, 0, message.Length);
                    var readTask = tcpStream.ReadAsync(receiveBuffer, 0, receiveBuffer.Length);

                    Task.WaitAll(writeTask, readTask);

                    //first is always 0x65
                    if (receiveBuffer[0] != 0x65)
                    {
                        Logger.LogWarning("Received unidentified message from Easy PLC");
                        return null;
                    }

                    int contentLength = receiveBuffer[1]; //Second byte is content length
                    var contentForCrc = receiveBuffer.SubArray(1, contentLength - 1);

                    var crc = calculateCrc(contentForCrc);
                    if (crc[0] == receiveBuffer[contentLength] && crc[1] == receiveBuffer[contentLength + 1])
                    {
                        return receiveBuffer.SubArray(2, contentLength - 2);
                    }

                    Logger.LogWarning("CRC for PLC message was incorrect");
                    return null;
                }
                catch (Exception x)
                {
                    Logger.LogError(x, "Error connecting Easy PLC");
                    return null;
                }
            }
        }
        private bool okResponse(byte[] reponse) 
        {
            return (reponse.Length == 8 &&
               reponse[0] == 0x65 &&
               reponse[1] == 0x06 &&
               reponse[2] == 0x00 &&
               reponse[3] == 0x00 &&
               reponse[4] == 0x00 &&
               reponse[5] == 0x41 &&
               reponse[6] == 0x48 &&
               reponse[7] == 0x30);
        }
        private bool okWriteResponse(byte[] reponse)
        {
            return (reponse.Length == 7 &&
               reponse[0] == 0x65 &&
               reponse[1] == 0x05 &&
               reponse[2] == 0x00 &&
               reponse[3] == 0x00 &&
               reponse[4] == 0x00 &&
               reponse[5] == 0x00 &&
               reponse[6] == 0xcc);
        }
        private byte[] calculateCrc(byte[] data)
        {
            var crc16 = new int[] { 0x0000, 0xc0c1, 0xc181, 0x0140, 0xc301, 0x03c0, 0x0280, 0xc241,
                                    0xc601, 0x06c0, 0x0780, 0xc741, 0x0500, 0xc5c1, 0xc481, 0x0440,
                                    0xcc01, 0x0cc0, 0x0d80, 0xcd41, 0x0f00, 0xcfc1, 0xce81, 0x0e40,
                                    0x0a00, 0xcac1, 0xcb81, 0x0b40, 0xc901, 0x09c0, 0x0880, 0xc841,
                                    0xd801, 0x18c0, 0x1980, 0xd941, 0x1b00, 0xdbc1, 0xda81, 0x1a40,
                                    0x1e00, 0xdec1, 0xdf81, 0x1f40, 0xdd01, 0x1dc0, 0x1c80, 0xdc41,
                                    0x1400, 0xd4c1, 0xd581, 0x1540, 0xd701, 0x17c0, 0x1680, 0xd641,
                                    0xd201, 0x12c0, 0x1380, 0xd341, 0x1100, 0xd1c1, 0xd081, 0x1040,
                                    0xf001, 0x30c0, 0x3180, 0xf141, 0x3300, 0xf3c1, 0xf281, 0x3240,
                                    0x3600, 0xf6c1, 0xf781, 0x3740, 0xf501, 0x35c0, 0x3480, 0xf441,
                                    0x3c00, 0xfcc1, 0xfd81, 0x3d40, 0xff01, 0x3fc0, 0x3e80, 0xfe41,
                                    0xfa01, 0x3ac0, 0x3b80, 0xfb41, 0x3900, 0xf9c1, 0xf881, 0x3840,
                                    0x2800, 0xe8c1, 0xe981, 0x2940, 0xeb01, 0x2bc0, 0x2a80, 0xea41,
                                    0xee01, 0x2ec0, 0x2f80, 0xef41, 0x2d00, 0xedc1, 0xec81, 0x2c40,
                                    0xe401, 0x24c0, 0x2580, 0xe541, 0x2700, 0xe7c1, 0xe681, 0x2640,
                                    0x2200, 0xe2c1, 0xe381, 0x2340, 0xe101, 0x21c0, 0x2080, 0xe041,
                                    0xa001, 0x60c0, 0x6180, 0xa141, 0x6300, 0xa3c1, 0xa281, 0x6240,
                                    0x6600, 0xa6c1, 0xa781, 0x6740, 0xa501, 0x65c0, 0x6480, 0xa441,
                                    0x6c00, 0xacc1, 0xad81, 0x6d40, 0xaf01, 0x6fc0, 0x6e80, 0xae41,
                                    0xaa01, 0x6ac0, 0x6b80, 0xab41, 0x6900, 0xa9c1, 0xa881, 0x6840,
                                    0x7800, 0xb8c1, 0xb981, 0x7940, 0xbb01, 0x7bc0, 0x7a80, 0xba41,
                                    0xbe01, 0x7ec0, 0x7f80, 0xbf41, 0x7d00, 0xbdc1, 0xbc81, 0x7c40,
                                    0xb401, 0x74c0, 0x7580, 0xb541, 0x7700, 0xb7c1, 0xb681, 0x7640,
                                    0x7200, 0xb2c1, 0xb381, 0x7340, 0xb101, 0x71c0, 0x7080, 0xb041,
                                    0x5000, 0x90c1, 0x9181, 0x5140, 0x9301, 0x53c0, 0x5280, 0x9241,
                                    0x9601, 0x56c0, 0x5780, 0x9741, 0x5500, 0x95c1, 0x9481, 0x5440,
                                    0x9c01, 0x5cc0, 0x5d80, 0x9d41, 0x5f00, 0x9fc1, 0x9e81, 0x5e40,
                                    0x5a00, 0x9ac1, 0x9b81, 0x5b40, 0x9901, 0x59c0, 0x5880, 0x9841,
                                    0x8801, 0x48c0, 0x4980, 0x8941, 0x4b00, 0x8bc1, 0x8a81, 0x4a40,
                                    0x4e00, 0x8ec1, 0x8f81, 0x4f40, 0x8d01, 0x4dc0, 0x4c80, 0x8c41,
                                    0x4400, 0x84c1, 0x8581, 0x4540, 0x8701, 0x47c0, 0x4680, 0x8641,
                                    0x8201, 0x42c0, 0x4380, 0x8341, 0x4100, 0x81c1, 0x8081, 0x4040 };

            var crc = 0x0000;

            foreach (var b in data)
                crc = (crc >> 8) ^ crc16[(crc ^ b) & 0xFF];

            return BitConverter.GetBytes(crc);
        }
        private void readOutputs(int netId = 1, int expansion = 0) 
        {
            //TODO inject netid to cmd
            var cmd = new byte[] { 0x45, 0x07, 0x01, 0x00, 0x01, 0x00, 0x00, 0x6d, 0xb7 };
            var outputStates = sendAndReceive(cmd);
            updateStates(IOType.Output, outputStates, netId, expansion);
           
        }
        private void readInputs(int netId = 1, int expansion = 0) 
        {
            //TODO inject netid to cmd
            var cmd = new byte[] { 0x45, 0x07, 0x01, 0x00, 0x00, 0x00, 0x00, 0x3c, 0x77 };
            var inputStates = sendAndReceive(cmd);
            updateStates(IOType.Input, inputStates, netId, expansion);
        }
        private void readMarkers(int netId = 1, int expansion = 0)
        {
            //TODO inject netid to cmd
            var cmd = new byte[] { 0x45, 0x07, 0x01, 0x00, 0x0a, 0x00, 0x06, 0x9c, 0x77 };
            var markerStates = sendAndReceive(cmd);
            markerStates = markerStates.SubArray(2); //skip first two
            updateStates(IOType.Marker, markerStates, netId, expansion);
        }
        private void updateStates(IOType type, byte[] states, int netId, int expansion) 
        {
            var bits = new System.Collections.BitArray(states);
            for (ushort i = 1; i <= bits.Length; i++)
            {
                string plcItem = createName(type, netId, i, expansion);
                bool state = bits.Get(i - 1);

                var reportTemplate = ReportTemplates.FirstOrDefault(x => x.Address == plcItem);
                bool valueChanged = false;

                if (_states.ContainsKey(plcItem))
                {
                    if (_states[plcItem] != state)
                        valueChanged = true;

                    _states[plcItem] = state;
                }
                else 
                {
                    _states.Add(plcItem, state);
                    valueChanged = true;
                }

                if (valueChanged && reportTemplate != null) 
                {
                    SendReport(this, new Report()
                    {
                        Id = reportTemplate.Id,
                        TimeStamp = DateTime.UtcNow.ToEpoch(),
                        Value = new ValueModel(state),
                        Filter = ""
                    });
                }
            }
        }
        private string createName(IOType type, int netId, int idx, int expansion = 0) 
        {
            var name = "";
            switch (type) 
            {
                case IOType.Input:
                    name = "I";
                    break;
                case IOType.Output:
                    name = "Q";
                    break;
                case IOType.Marker:
                    name = "M";
                    break;
            }

            return $"{name}-{netId}-{expansion}-{idx}";
        }
        private bool writeMarker(int marker, bool value, int netId = 1, int expansion = 0) 
        {
            byte markerByte = (byte)(marker - 1);
            byte netIdByte = (byte)(netId + 32);
            byte v = value ? (byte)0x01 : (byte)0x00;

            var cmd = new List<byte> { 0x08, netIdByte, 0x00, 0x04, markerByte, 0x00, v };
            byte[] crc = calculateCrc(cmd.ToArray());

            cmd.Insert(0, 0x45);
            cmd.Add(crc[0]);
            cmd.Add(crc[1]);

            var result = sendAndReceive(cmd.ToArray());
            return okWriteResponse(result);
        }

        #endregion
    }

    internal enum IOType 
    {
        Marker,
        Input,
        Output
    }
}
