/*
 * Copyright (c) 2006-2016, openmetaverse.co
 * Copyright (c) 2025, Sjofn LLC.
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without 
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.co nor the names 
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" 
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF 
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
 * POSSIBILITY OF SUCH DAMAGE.
 */

using LibreMetaverse.Packets;

namespace LibreMetaverse
{
    /// <summary>
    /// Throttles the network traffic for various different traffic types.
    /// Access this class through GridClient.Throttle
    /// </summary>
    public class AgentThrottle
    {
        // Ceilings per channel, in bits per second: what the Second Life viewer sends at its own
        // maximum of 6,000 kbps (llviewerthrottle.cpp, extrapolated past its 1,000 kbps preset).
        // They were 150,000 / 170,000 / 34,000 / 34,000 / 1,338,000 / 446,000 / 220,000, and the
        // object channel's ceiling sat below what the reference sends at its DEFAULT 3,000 kbps
        // (1,528 kbps), so no bandwidth setting could load a region faster than it. Task also
        // allows the texture and asset shares of that maximum, which a viewer fetching both over
        // HTTP may move to objects; the simulator applies its own limits either way.
        public const float MaxResend = 614400f;
        public const float MaxLand = 409600f;
        public const float MaxWind = 81920f;
        public const float MaxCloud = 81920f;
        public const float MaxTask = 2099200f + 2099200f + 757760f;
        public const float MaxTexture = 2099200f;
        public const float MaxAsset = 757760f;

        /// <summary>Maximum bits per second for resending unacknowledged packets</summary>
        public float Resend
        {
            get => resend;
            set
            {
                if (value > MaxResend) resend = MaxResend;
                else if (value < 10000.0f) resend = 10000.0f;
                else resend = value;
            }
        }
        /// <summary>Maximum bits per second for LayerData terrain</summary>
        public float Land
        {
            get => land;
            set
            {
                if (value > MaxLand) land = MaxLand;
                else if (value < 0.0f) land = 0.0f; // We don't have control of these so allow throttling to 0
                else land = value;
            }
        }
        /// <summary>Maximum bits per second for LayerData wind data</summary>
        public float Wind
        {
            get => wind;
            set
            {
                if (value > MaxWind) wind = MaxWind;
                else if (value < 0.0f) wind = 0.0f; // We don't have control of these so allow throttling to 0
                else wind = value;
            }
        }
        /// <summary>Maximum bits per second for LayerData clouds</summary>
        public float Cloud
        {
            get => cloud;
            set
            {
                if (value > MaxCloud) cloud = MaxCloud;
                else if (value < 0.0f) cloud = 0.0f; // We don't have control of these so allow throttling to 0
                else cloud = value;
            }
        }
        /// <summary>Unknown, includes object data</summary>
        public float Task
        {
            get => task;
            set
            {
                if (value > MaxTask) task = MaxTask;
                else if (value < 4000.0f) task = 4000.0f;
                else task = value;
            }
        }
        /// <summary>Maximum bits per second for textures</summary>
        public float Texture
        {
            get => texture;
            set
            {
                if (value > MaxTexture) texture = MaxTexture;
                else if (value < 4000.0f) texture = 4000.0f;
                else texture = value;
            }
        }
        /// <summary>Maximum bits per second for downloaded assets</summary>
        public float Asset
        {
            get => asset;
            set
            {
                if (value > MaxAsset) asset = MaxAsset;
                else if (value < 10000.0f) asset = 10000.0f;
                else asset = value;
            }
        }

        /// <summary>Maximum bits per second the entire connection, divided up
        /// between individual streams using default multipliers</summary>
        public float Total
        {
            get => Resend + Land + Wind + Cloud + Task + Texture + Asset;
            set
            {
                // Sane initial values
                Resend = (value * 0.1f);
                Land = (float)(value * 0.52f / 3f);
                Wind = (float)(value * 0.05f);
                Cloud = (float)(value * 0.05f);
                Task = (float)(value * 0.704f / 3f);
                Texture = (float)(value * 0.704f / 3f);
                Asset = (float)(value * 0.484f / 3f);
            }
        }

        private readonly GridClient? Client;
        private float resend;
        private float land;
        private float wind;
        private float cloud;
        private float task;
        private float texture;
        private float asset;

        /// <summary>
        /// Default constructor, uses a default high total of 1500 KBps (1536000)
        /// </summary>
        public AgentThrottle(GridClient client)
        {
            Client = client;
            Total = 1536000.0f;
        }

        /// <summary>
        /// Constructor that decodes an existing AgentThrottle packet in to
        /// individual values
        /// </summary>
        /// <param name="data">Reference to the throttle data in an AgentThrottle
        /// packet</param>
        /// <param name="pos">Offset position to start reading at in the 
        /// throttle data</param>
        /// <remarks>This is generally not needed in clients as the server will
        /// never send a throttle packet to the client</remarks>
        public AgentThrottle(byte[] data, int pos)
        {
            // Decode 7 little-endian floats from the provided byte array
            Resend = Utils.ReadSingleLittleEndian(data, pos); pos += 4;
            Land = Utils.ReadSingleLittleEndian(data, pos); pos += 4;
            Wind = Utils.ReadSingleLittleEndian(data, pos); pos += 4;
            Cloud = Utils.ReadSingleLittleEndian(data, pos); pos += 4;
            Task = Utils.ReadSingleLittleEndian(data, pos); pos += 4;
            Texture = Utils.ReadSingleLittleEndian(data, pos); pos += 4;
            Asset = Utils.ReadSingleLittleEndian(data, pos);
        }

        /// <summary>
        /// Send an AgentThrottle packet to the current server using the 
        /// current values
        /// </summary>
        public void Set()
        {
            var sim = Client?.Network?.CurrentSim;
            if (sim == null) return;
            Set(sim);
        }

        /// <summary>
        /// Send an AgentThrottle packet to the specified server using the
        /// current values
        /// </summary>
        public void Set(Simulator? simulator)
        {
            if (Client == null || simulator == null) return;

            AgentThrottlePacket throttle = new AgentThrottlePacket
            {
                AgentData =
                {
                    AgentID = Client.Self.AgentID,
                    SessionID = Client.Self.SessionID,
                    CircuitCode = Client.Network.CircuitCode
                },
                Throttle =
                {
                    GenCounter = 0,
                    Throttles = ToBytes()
                }
            };

            Client.Network.SendPacket(throttle, simulator);

            // Synchronise the outgoing UDP throttle with the new rates.
            Client.Network.UpdateUdpThrottle(this);
        }

        /// <summary>
        /// Convert the current throttle values to a byte array that can be put
        /// in an AgentThrottle packet
        /// </summary>
        /// <returns>Byte array containing all the throttle values</returns>
        public byte[] ToBytes()
        {
            var data = new byte[7 * 4];
            int i = 0;

            Utils.WriteSingleLittleEndian(data, i, Resend); i += 4;
            Utils.WriteSingleLittleEndian(data, i, Land); i += 4;
            Utils.WriteSingleLittleEndian(data, i, Wind); i += 4;
            Utils.WriteSingleLittleEndian(data, i, Cloud); i += 4;
            Utils.WriteSingleLittleEndian(data, i, Task); i += 4;
            Utils.WriteSingleLittleEndian(data, i, Texture); i += 4;
            Utils.WriteSingleLittleEndian(data, i, Asset); i += 4;

            return data;
        }
    }
}
