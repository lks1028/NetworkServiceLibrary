using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
//using System.Threading.Tasks;

namespace NetworkServiceLibrary
{
    public class CUserToken
    {
        public Socket socket { get; set; }

        public SocketAsyncEventArgs receive_event_args { get; private set; }
        public SocketAsyncEventArgs send_event_args { get; private set; }

        // 바이트를 패킷 형식으로 해석해주는 해석기
        private CMessageResolver message_resolver;

        // session 객체. 어플리케이션단에서 구현해서 사용
        private IPeer peer;

        // 전송할 패킷을 보관하는 큐
        private Queue<CPacket> sending_queue;
        // sending_queue lock 처리에 사용되는 객체
        private object cs_sending_queue;
        private static int sent_count = 0;
        private static object cs_count = new object();

        public CUserToken()
        {
            cs_sending_queue = new object();
            message_resolver = new CMessageResolver();
            peer = null;
            sending_queue = new Queue<CPacket>();
        }

        public void set_peer(IPeer peer)
        {
            this.peer = peer;
        }

        public void set_event_args(SocketAsyncEventArgs receive_event_args, SocketAsyncEventArgs send_event_args)
        {
            this.receive_event_args = receive_event_args;
            this.send_event_args = send_event_args;
        }

        /// <summary>
		///	이 매소드에서 직접 바이트 데이터를 해석해도 되지만 Message resolver클래스를 따로 둔 이유는
		///	추후에 확장성을 고려하여 다른 resolver를 구현할 때 CUserToken클래스의 코드 수정을 최소화 하기 위함이다.
		/// </summary>
		/// <param name="buffer"></param>
		/// <param name="offset"></param>
		/// <param name="transfered"></param>
		public void on_receive(byte[] buffer, int offset, int transfered)
        {
            message_resolver.on_receive(buffer, offset, transfered, on_message);
        }

        private void on_message(Const<byte[]> buffer)
        {
            if (peer != null)
                peer.on_message(buffer);
        }

        public void on_removed()
        {
            sending_queue.Clear();

            if (peer != null)
                peer.on_removed();
        }

        /// <summary>  
        /// 패킷을 전송한다.  
        /// 큐가 비어 있을 경우에는 큐에 추가한 뒤 바로 SendAsync매소드를 호출하고,  
        /// 데이터가 들어있을 경우에는 새로 추가만 한다.  
        ///   
        /// 큐잉된 패킷의 전송 시점 :  
        ///     현재 진행중인 SendAsync가 완료되었을 때 큐를 검사하여 나머지 패킷을 전송한다.  
        /// </summary>  
        /// <param name="msg"></param>
        public void send(CPacket msg)
        {
            lock (cs_sending_queue)
            {
                // 큐가 비어 있다면 큐에 추가하고 바로 비동기 전송 메소드를 호출
                if (sending_queue.Count <= 0)
                {
                    sending_queue.Enqueue(msg);
                    start_send();
                    return;
                }

                // 큐에 무언가가 들어 있다면 아직 이전 전송이 완료되지 않은 상태이므로 큐에 추가만 하고 리턴한다.  
                // 현재 수행중인 SendAsync가 완료된 이후에 큐를 검사하여 데이터가 있으면 SendAsync를 호출하여 전송해줄 것이다.
                Console.WriteLine("Queue is not empty. Copy and Enqueue a msg. protocol id : " + msg.protocol_id);
                sending_queue.Enqueue(msg);
            }
        }

        /// <summary>  
        /// 비동기 전송을 시작한다.  
        /// </summary>
        private void start_send()
        {
            lock (cs_sending_queue)
            {
                // 전송이 아직 완료된 상태가 아니므로 데이터만 가져오고 큐에서 제거하진 않는다.
                CPacket msg = sending_queue.Peek();

                // 헤더에 패킷 사이즈를 기록한다
                msg.record_size();

                // 보낼 패킷 사이즈 만큼 버퍼 크기를 설정
                send_event_args.SetBuffer(send_event_args.Offset, msg.position);
                // 패킷 내용을 SocketAsyncEventArgs 버퍼에 복사
                Array.Copy(msg.buffer, 0, send_event_args.Buffer, send_event_args.Offset, msg.position);

                // 비동기 전송 시작
                bool pending = socket.SendAsync(send_event_args);
                if (!pending)
                {
                    process_send(send_event_args);
                }
            }
        }

        /// <summary>
		/// 비동기 전송 완료시 호출되는 콜백 매소드.
		/// </summary>
		/// <param name="e"></param>
        public void process_send(SocketAsyncEventArgs e)
        {
            if (e.BytesTransferred <= 0 || e.SocketError != SocketError.Success)
            {
                return;
            }

            lock (cs_sending_queue)
            {
                // count가 0 이하일 경우
                if (sending_queue.Count <= 0)
                {
                    throw new Exception("Sending queue count is less than zero");
                }

                // 패킷 하나를 다 못보낸 경우
                int size = sending_queue.Peek().position;
                if (e.BytesTransferred != size)
                {
                    string error = string.Format("Need to send more! transferred {0},  packet size {1}", e.BytesTransferred, size);
                    Console.WriteLine(error);
                    return;
                }

                lock (cs_count)
                {
                    ++sent_count;
                    {
                        Console.WriteLine(string.Format("process send : {0}, transferred {1}, sent count {2}",
                            e.SocketError, e.BytesTransferred, sent_count));
                    }
                }

                //Console.WriteLine(string.Format("process send : {0}, transferred {1}, sent count {2}",
                //	e.SocketError, e.BytesTransferred, sent_count));

                // 전송 완료된 패킷을 큐에서 제거한다.
                //CPacket packet = this.sending_queue.Dequeue();
                //CPacket.destroy(packet);
                sending_queue.Dequeue();

                // 아직 전송하지 않은 대기중인 패킷이 있다면 다시한번 전송을 요청한다.
                if (sending_queue.Count > 0)
                {
                    start_send();
                }
            }
        }

        public void disconnect()
        {
            // close the socket associated with the client
            try
            {
                this.socket.Shutdown(SocketShutdown.Send);
            }
            // throws if client process has already closed
            catch (Exception) { }
            this.socket.Close();
        }

        public void start_keepalive()
        {
            System.Threading.Timer keepalive = new System.Threading.Timer((object e) =>
            {
                CPacket msg = CPacket.create(0);
                msg.push(0);
                send(msg);
            }, null, 0, 3000);
        }
    }
}
