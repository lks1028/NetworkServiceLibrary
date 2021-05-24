using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
//using System.Threading.Tasks;

namespace NetworkServiceLibrary
{
    class CListener
    {
        // 비동기 Accept를 위한 EventArgs
        SocketAsyncEventArgs accept_args;

        // 클라이언트의 접속을 처리할 소켓
        Socket listen_socket;

        // Accept 처리의 순서를 제어하기 위한 이벤트 변수.
        AutoResetEvent flow_control_event;

        // 새로운 클라이언트가 접속했을 때 호출되는 콜백
        public delegate void NewclientHandler(Socket client_socket, object token);
        public NewclientHandler callback_on_newclient;

        public CListener()
        {
            this.callback_on_newclient = null;
        }

        public void start(string host, int port, int backlog)
        {
            // 소켓 생성
            this.listen_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPAddress address;
            if (host == "0.0.0.0")
                address = IPAddress.Any;
            else
                address = IPAddress.Parse(host);

            IPEndPoint endpoint = new IPEndPoint(address, port);

            try
            {
                // 소켓에 host 정보를 바인딩 시킨뒤 Listen 메소드를 호출하여 준비
                listen_socket.Bind(endpoint);
                listen_socket.Listen(backlog);

                accept_args = new SocketAsyncEventArgs();
                accept_args.Completed += new EventHandler<SocketAsyncEventArgs>(on_accept_completed);

                // 클라이언트가 들어오기를 기다립니다.  
                // 비동기 매소드 이므로 블로킹 되지 않고 바로 리턴됩니다.  
                // 콜백 매소드를 통해서 접속 통보를 처리하면 됩니다.
                //listen_socket.AcceptAsync(accept_args);

                Thread listen_therad = new Thread(do_listen);
                listen_therad.Start();
            }
            catch (Exception e)
            {

                throw;
            }
        }

        private void do_listen()
        {
            // accept 처리 제어를 위해 이벤트 객체를 생성합니다.
            flow_control_event = new AutoResetEvent(false);

            while (true)
            {
                // SocketAsyncEventArgs를 재사용 하기 위해서 null로 만든다.
                accept_args.AcceptSocket = null;

                bool pending = true;
                try
                {
                    // 비동기 accept를 호출하여 클라이언트의 접속을 받아들입니다.  
                    // 비동기 매소드 이지만 동기적으로 수행이 완료될 경우도 있으니  
                    // 리턴값을 확인하여 분기시켜야 합니다.
                    pending = listen_socket.AcceptAsync(accept_args);
                }
                catch (Exception e)
                {
                    continue;
                }

                // 즉시 완료 되면 이벤트가 발생하지 않으므로 리턴값이 false일 경우 콜백 매소드를 직접 호출해 줍니다.  
                // pending상태라면 비동기 요청이 들어간 상태이므로 콜백 매소드를 기다리면 됩니다.  
                // http://msdn.microsoft.com/ko-kr/library/system.net.sockets.socket.acceptasync%28v=vs.110%29.aspx  
                if (!pending)
                    on_accept_completed(null, accept_args);

                // 클라이언트 접속 처리가 완료되면 이벤트 객체의 신호를 전달받아 다시 루프를 수행하도록 합니다. 
                flow_control_event.WaitOne();
            }
        }

        private void on_accept_completed(object sender, SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                // 새로 생긴 소켓 보관
                Socket client_socket = e.AcceptSocket;

                // 다음 연결을 받는다.
                flow_control_event.Set();

                // 이 클래스에서는 accept까지의 역할만 수행하고 클라이언트의 접속 이후의 처리는  
                // 외부로 넘기기 위해서 콜백 매소드를 호출해 주도록 합니다.  
                // 이유는 소켓 처리부와 컨텐츠 구현부를 분리하기 위함입니다.  
                // 컨텐츠 구현부분은 자주 바뀔 가능성이 있지만, 소켓 Accept부분은 상대적으로 변경이 적은 부분이기 때문에  
                // 양쪽을 분리시켜주는것이 좋습니다.  
                // 또한 클래스 설계 방침에 따라 Listen에 관련된 코드만 존재하도록 하기 위한 이유도 있습니다.
                if (callback_on_newclient != null)
                    callback_on_newclient(client_socket, e.UserToken);

                return;
            }
            else
            {
                // Accept 실패 처리
                Console.WriteLine("Failed to accept client");
            }

            // 다음 연결을 받아들인다.
            flow_control_event.Set();
        }
    }
}
