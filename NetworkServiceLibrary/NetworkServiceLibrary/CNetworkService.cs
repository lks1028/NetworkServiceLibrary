﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
//using System.Threading.Tasks;

namespace NetworkServiceLibrary
{
    public class CNetworkService
    {
        int connected_count;
        CListener client_listner;

        SocketAsyncEventArgsPool receive_event_args_pool;
        SocketAsyncEventArgsPool send_event_args_pool;
        BufferManager buffer_manager;

        public delegate void SessionHandler(CUserToken token);
        public SessionHandler session_created_callback { get; set; }

        int max_connections;
        int buffer_size;
        readonly int pre_alloc_count = 2;

        public CNetworkService()
        {
            connected_count = 0;
            session_created_callback = null;
        }

        public void listen(string host, int port, int backlog)
        {
            client_listner = new CListener();
            client_listner.callback_on_newclient += on_new_client;
            client_listner.start(host, port, backlog);
        }

        public void initialize()
        {
            max_connections = 10000;
            buffer_size = 1024;

            receive_event_args_pool = new SocketAsyncEventArgsPool(max_connections);
            send_event_args_pool = new SocketAsyncEventArgsPool(max_connections);
            buffer_manager = new BufferManager(max_connections * buffer_size * pre_alloc_count, buffer_size);

            buffer_manager.InitBuffer();
            SocketAsyncEventArgs arg;

            for (int i = 0; i < max_connections; i++)
            {
                // 동일한 소켓에 대고 send, receive를 하므로
                // user token은 세션별로 하나씩만 만들어 놓고 
                // receive, send EventArgs에서 동일한 token을 참조하도록 구성한다.
                CUserToken token = new CUserToken();

                // receive pool
                {
                    arg = new SocketAsyncEventArgs();
                    arg.Completed += new EventHandler<SocketAsyncEventArgs>(receive_completed);
                    arg.UserToken = token;

                    buffer_manager.SetBuffer(arg);

                    receive_event_args_pool.Push(arg);
                }

                // send pool
                {
                    arg = new SocketAsyncEventArgs();
                    arg.Completed += new EventHandler<SocketAsyncEventArgs>(send_completed);
                    arg.UserToken = token;

                    buffer_manager.SetBuffer(arg);

                    send_event_args_pool.Push(arg);
                }
            }
        }

        /// <summary>
		/// 클라이언트에서 원격 서버에 접속 성공 했을 때 호출됩니다.
		/// </summary>
		/// <param name="socket"></param>
        public void on_connect_completed(Socket socket, CUserToken token)
        {
            // SocketAsyncEventArgsPool에서 빼오지 않고 그때 그때 할당해서 사용한다.
            // 풀은 서버에서 클라이언트와의 통신용으로만 쓰려고 만든것이기 때문이다.
            // 클라이언트 입장에서 서버와 통신을 할 때는 접속한 서버당 두개의 EventArgs만 있으면 되기 때문에 그냥 new해서 쓴다.
            // 서버간 연결에서도 마찬가지이다.
            // 풀링처리를 하려면 c->s로 가는 별도의 풀을 만들어서 써야 한다.
            SocketAsyncEventArgs receive_event_arg = new SocketAsyncEventArgs();
            receive_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(receive_completed);
            receive_event_arg.UserToken = token;
            receive_event_arg.SetBuffer(new byte[1024], 0, 1024);

            SocketAsyncEventArgs send_event_arg = new SocketAsyncEventArgs();
            send_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(send_completed);
            send_event_arg.UserToken = token;
            send_event_arg.SetBuffer(new byte[1024], 0, 1024);

            begin_receive(socket, receive_event_arg, send_event_arg);
        }

        /// <summary>
        /// 새로운 클라이언트가 접속 성공 했을 때 호출됩니다.
        /// AcceptAsync의 콜백 매소드에서 호출되며 여러 스레드에서 동시에 호출될 수 있기 때문에 공유자원에 접근할 때는 주의해야 합니다.
        /// </summary>
        /// <param name="client_socket"></param>
        private void on_new_client(Socket client_socket, object token)
        {
            Interlocked.Increment(ref this.connected_count);

            Console.WriteLine(string.Format("[{0}] A client connected. handle {1},  count {2}",
                Thread.CurrentThread.ManagedThreadId, client_socket.Handle,
                this.connected_count));

            // 풀에서 하나 꺼내와 사용
            SocketAsyncEventArgs receive_args = receive_event_args_pool.Pop();
            SocketAsyncEventArgs send_args = send_event_args_pool.Pop();

            // SocketAsyncEventArgs를 생성할 때 만들어 두었던 CUserToken을 꺼내와서   
            // 콜백 매소드의 파라미터로 넘겨줍니다.  
            if (session_created_callback != null)
            {
                CUserToken user_token = receive_args.UserToken as CUserToken;
                session_created_callback(user_token);
            }

            // 클라이언트에서 데이터 수신 준비
            begin_receive(client_socket, receive_args, send_args);
        }

        private void begin_receive(Socket socket, SocketAsyncEventArgs receive_args, SocketAsyncEventArgs send_args)
        {
            // receive_args, send_args 아무곳에서나 꺼내와도 된다. 둘다 동일한 CUserToken을 물고 있다.
            CUserToken token = receive_args.UserToken as CUserToken;
            token.set_event_args(receive_args, send_args);

            // 생성된 클라이언트 소켓을 보관해 놓고 통신할때 사용
            token.socket = socket;

            // 데이터를 받을 수 있도록 소켓 매소드를 호출해준다.  
            // 비동기로 수신할 경우 워커 스레드에서 대기중으로 있다가 Completed에 설정해놓은 매소드가 호출된다.  
            // 동기로 완료될 경우에는 직접 완료 매소드를 호출해줘야 한다.  
            bool pending = socket.ReceiveAsync(receive_args);
            if (!pending)
            {
                process_receive(receive_args);
            }
        }

        private void receive_completed(object sender, SocketAsyncEventArgs e)
        {
            if (e.LastOperation == SocketAsyncOperation.Receive)
            {
                process_receive(e);
                return;
            }

            throw new ArgumentException("The last operation completed on the socket was not a receive.");
        }

        private void send_completed(object sender, SocketAsyncEventArgs e)
        {
            CUserToken token = e.UserToken as CUserToken;
            token.process_send(e);
        }

        private void process_receive(SocketAsyncEventArgs e)
        {
            CUserToken token = e.UserToken as CUserToken;
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                // 이후의 작업은 CUserToken에게
                token.on_receive(e.Buffer, e.Offset, e.BytesTransferred);

                // 다음 메세지 수신을 위해 다시 ReceiveAsync 호출
                bool pending = token.socket.ReceiveAsync(e);
                if (!pending)
                {
                    process_receive(e);
                }
            }
            else
            {
                Console.WriteLine(string.Format("error {0},  transferred {1}", e.SocketError, e.BytesTransferred));
                close_clientsocket(token);
            }
        }

        public void close_clientsocket(CUserToken token)
        {
            token.on_removed();

            // Free the SocketAsyncEventArg so they can be reused by another client
            // 버퍼는 반환할 필요가 없다. SocketAsyncEventArg가 버퍼를 물고 있기 때문에
            // 이것을 재사용 할 때 물고 있는 버퍼를 그대로 사용하면 되기 때문이다.
            if (receive_event_args_pool != null)
                receive_event_args_pool.Push(token.receive_event_args);

            if (send_event_args_pool != null)
                send_event_args_pool.Push(token.send_event_args);
        }
    }
}
