﻿using System;
using System.Threading;
using KNXLib.Logging;

namespace KNXLib
{
    internal abstract class KnxReceiver
    {
        private static readonly ILog Logger = LogProvider.For<KnxReceiver>();

        private Thread _receiverThread;

        protected KnxReceiver(KnxConnection connection)
        {
            KnxConnection = connection;
        }

        protected KnxConnection KnxConnection { get; private set; }

        public abstract void ReceiverThreadFlow();

        public void Start()
        {
            _receiverThread = new Thread(ReceiverThreadFlow) { IsBackground = true };
            _receiverThread.Start();
        }

        public void Stop()
        {
            try
            {
                if (_receiverThread.ThreadState.Equals(ThreadState.Running))
                    _receiverThread.Abort();
            }
            catch
            {
                Thread.ResetAbort();
            }
        }

        protected void ProcessCEMI(KnxDatagram datagram, byte[] cemi)
        {
            try
            {
                // CEMI
                // +--------+--------+--------+--------+----------------+----------------+--------+----------------+
                // |  Msg   |Add.Info| Ctrl 1 | Ctrl 2 | Source Address | Dest. Address  |  Data  |      APDU      |
                // | Code   | Length |        |        |                |                | Length |                |
                // +--------+--------+--------+--------+----------------+----------------+--------+----------------+
                //   1 byte   1 byte   1 byte   1 byte      2 bytes          2 bytes       1 byte      2 bytes
                //
                //  Message Code    = 0x11 - a L_Data.req primitive
                //      COMMON EMI MESSAGE CODES FOR DATA LINK LAYER PRIMITIVES
                //          FROM NETWORK LAYER TO DATA LINK LAYER
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          | Data Link Layer Primitive | Message Code | Data Link Layer Service | Service Description | Common EMI Frame |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |        L_Raw.req          |    0x10      |                         |                     |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |                           |              |                         | Primitive used for  | Sample Common    |
                //          |        L_Data.req         |    0x11      |      Data Service       | transmitting a data | EMI frame        |
                //          |                           |              |                         | frame               |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |        L_Poll_Data.req    |    0x13      |    Poll Data Service    |                     |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |        L_Raw.req          |    0x10      |                         |                     |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          FROM DATA LINK LAYER TO NETWORK LAYER
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          | Data Link Layer Primitive | Message Code | Data Link Layer Service | Service Description |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Poll_Data.con    |    0x25      |    Poll Data Service    |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |                           |              |                         | Primitive used for  |
                //          |        L_Data.ind         |    0x29      |      Data Service       | receiving a data    |
                //          |                           |              |                         | frame               |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Busmon.ind       |    0x2B      |   Bus Monitor Service   |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Raw.ind          |    0x2D      |                         |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |                           |              |                         | Primitive used for  |
                //          |                           |              |                         | local confirmation  |
                //          |        L_Data.con         |    0x2E      |      Data Service       | that a frame was    |
                //          |                           |              |                         | sent (does not mean |
                //          |                           |              |                         | successful receive) |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Raw.con          |    0x2F      |                         |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+

                //  Add.Info Length = 0x00 - no additional info
                //  Control Field 1 = see the bit structure above
                //  Control Field 2 = see the bit structure above
                //  Source Address  = 0x0000 - filled in by router/gateway with its source address which is
                //                    part of the KNX subnet
                //  Dest. Address   = KNX group or individual address (2 byte)
                //  Data Length     = Number of bytes of data in the APDU excluding the TPCI/APCI bits
                //  APDU            = Application Protocol Data Unit - the actual payload including transport
                //                    protocol control information (TPCI), application protocol control
                //                    information (APCI) and data passed as an argument from higher layers of
                //                    the KNX communication stack
                //
                datagram.message_code = cemi[0];
                datagram.aditional_info_length = cemi[1];

                if (datagram.aditional_info_length > 0)
                {
                    datagram.aditional_info = new byte[datagram.aditional_info_length];
                    for (var i = 0; i < datagram.aditional_info_length; i++)
                    {
                        datagram.aditional_info[i] = cemi[2 + i];
                    }
                }

                datagram.control_field_1 = cemi[2 + datagram.aditional_info_length];
                datagram.control_field_2 = cemi[3 + datagram.aditional_info_length];
                datagram.source_address = KnxHelper.GetIndividualAddress(new[] { cemi[4 + datagram.aditional_info_length], cemi[5 + datagram.aditional_info_length] });

                datagram.destination_address =
                    KnxHelper.GetKnxDestinationAddressType(datagram.control_field_2).Equals(KnxHelper.KnxDestinationAddressType.INDIVIDUAL)
                        ? KnxHelper.GetIndividualAddress(new[] { cemi[6 + datagram.aditional_info_length], cemi[7 + datagram.aditional_info_length] })
                        : KnxHelper.GetGroupAddress(new[] { cemi[6 + datagram.aditional_info_length], cemi[7 + datagram.aditional_info_length] }, KnxConnection.ThreeLevelGroupAddressing);

                datagram.data_length = cemi[8 + datagram.aditional_info_length];
                datagram.apdu = new byte[datagram.data_length + 1];

                for (var i = 0; i < datagram.apdu.Length; i++)
                    datagram.apdu[i] = cemi[9 + i + datagram.aditional_info_length];

                datagram.data = KnxHelper.GetData(datagram.data_length, datagram.apdu);

                if (Logger.IsDebugEnabled())
                {
                    Logger.Debug(new string('-', 80));
                    Logger.Debug(BitConverter.ToString(cemi));
                    Logger.Debug("Event Header Length: " + datagram.header_length);
                    Logger.Debug("Event Protocol Version: " + datagram.protocol_version.ToString("x"));
                    Logger.Debug("Event Service Type: 0x" + BitConverter.ToString(datagram.service_type).Replace("-", string.Empty));
                    Logger.Debug("Event Total Length: " + datagram.total_length);

                    Logger.Debug("Event Message Code: " + datagram.message_code.ToString("x"));
                    Logger.Debug("Event Aditional Info Length: " + datagram.aditional_info_length);

                    if (datagram.aditional_info_length > 0)
                        Logger.Debug("Event Aditional Info: 0x" + BitConverter.ToString(datagram.aditional_info).Replace("-", string.Empty));

                    Logger.Debug("Event Control Field 1: " + Convert.ToString(datagram.control_field_1, 2));
                    Logger.Debug("Event Control Field 2: " + Convert.ToString(datagram.control_field_2, 2));
                    Logger.Debug("Event Source Address: " + datagram.source_address);
                    Logger.Debug("Event Destination Address: " + datagram.destination_address);
                    Logger.Debug("Event Data Length: " + datagram.data_length);
                    Logger.Debug("Event APDU: 0x" + BitConverter.ToString(datagram.apdu).Replace("-", string.Empty));
                    Logger.Debug("Event Data: " + datagram.data);
                    Logger.Debug(new string('-', 80));
                }

                if (datagram.message_code != 0x29)
                    return;

                var type = datagram.apdu[1] >> 4;

                switch (type)
                {
                    case 8:
                        KnxConnection.Event(datagram.destination_address, datagram.data);
                        break;
                    case 4:
                        KnxConnection.Status(datagram.destination_address, datagram.data);
                        break;
                }
            }
            catch
            {
                // ignore, missing warning information
            }
        }
    }
}
