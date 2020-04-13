using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Matrox.MatroxImagingLibrary;

namespace Matrox_MultiImageBuffer_Example
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region Fields
        private object _LockObj = new object();
        private MIL_ID _MilApplication;
        private MIL_ID[] _ImageBuffer;
        private ObservableCollection<MatroxCamera> m_MatroxCameraList;
        private bool _IsStop;
        private BitmapSource m_DisplayBitmapSource;
        private MIL_DIG_HOOK_FUNCTION_PTR _GrabCallbackFunc;

        public event PropertyChangedEventHandler PropertyChanged;
        #endregion

        #region Properties
        public ObservableCollection<MatroxCamera> MatroxCameraList
        {
            get
            {
                return m_MatroxCameraList;
            }
            private set
            {
                m_MatroxCameraList = value;
                RaisePropertyChanged("MatroxCameraList");
            }
        }
        public bool IsStop
        {
            get { return _IsStop; }
            set
            {
                _IsStop = value;
                RaisePropertyChanged("IsStop");
            }
        }
        public MatroxCamera CurrentUseCamera { get; set; }

        public BitmapSource DisplayBitmapSource
        {
            get { return m_DisplayBitmapSource; }
            set
            {
                m_DisplayBitmapSource = value;
                RaisePropertyChanged("DisplayBitmapSource");
            }
        }

        public int BufferCount { get; set; }
        #endregion


        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        #region Methods
        protected void RaisePropertyChanged(string pName)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(pName));
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            IsStop = true;
            MatroxCameraList = new ObservableCollection<MatroxCamera>();
            this.CameraOpen();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            this.CameraClose();
        }
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            lock (_LockObj)
            {
                if (IsStop)
                {
                    if (CurrentUseCamera == null)
                    {
                        MessageBox.Show("Select camera first.");
                        return;
                    }
                    if (BufferCount == 0)
                    {
                        MessageBox.Show("Buffer count is 1 at least.");
                        return;
                    }
                    this.CameraGrabStart();
                    IsStop = !IsStop;
                }
                else
                {
                    this.CameraGrabStop();
                    IsStop = !IsStop;
                }
            }
        }

        #region Camera Methods
        private void CameraOpen()
        {
            try
            {
                //mil app open
                MIL.MappAlloc(MIL.M_NULL, MIL.M_DEFAULT, ref _MilApplication);
                //mil throw 관리
                MIL.MappControl(_MilApplication, MIL.M_ERROR, MIL.M_THROW_EXCEPTION);

                //설치된 보드 드라이버 수 가져오기
                var insSysCount = MIL.MappInquire(MIL.M_INSTALLED_SYSTEM_COUNT);
                for (int i = 0; i < insSysCount; i++)
                {
                    MIL_ID tmpSystem = MIL.M_NULL;
                    StringBuilder sb = new StringBuilder();

                    //보드 종류 문자열로 뽑아내기
                    MIL.MappInquire(_MilApplication, MIL.M_INSTALLED_SYSTEM_DESCRIPTOR + i, sb);

                    var boardCount = 0;
                    //같은 종류의 보드 몇 개까지 존재하는지 확인
                    while (sb.ToString() != "M_SYSTEM_HOST")
                    {
                        MIL_ID systemId = MIL.M_NULL;
                        try
                        {
                            //보드 alloc
                            MIL.MsysAlloc(sb.ToString(), boardCount, MIL.M_DEFAULT, ref systemId);
                        }
                        catch
                        {
                            //해당 보드는 메인보드에서 인식되지 않음 (없음)
                            break;
                        }
                        //alloc된 보드 추가
                        var cam = new MatroxCamera();

                        //Digitizer 몇개 존재하는지 확인
                        var digCount = MIL.MsysInquire(systemId, MIL.M_DIGITIZER_NUM);
                        //임의의 dcf file 추가 (원하는 dcf로 변경)
                        var dcfPath = AppDomain.CurrentDomain.BaseDirectory + @"\camfile.dcf";

                        //보드에 연결된 카메라 수만큼 돎
                        for (int ii = 0; ii < digCount; ii++)
                        {
                            MIL_ID digitId = MIL.M_NULL;
                            //digitizer alloc
                            MIL.MdigAlloc(systemId, ii, dcfPath, MIL.M_DEFAULT, ref digitId);
                            cam.SystemID = systemId;
                            cam.DigitizerID = digitId;
                        }
                        MatroxCameraList.Add(cam);
                        boardCount++;
                    }
                }
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message + "\n" + err.StackTrace);
            }
        }
        private void CameraClose()
        {
            try
            {
                if (!IsStop) this.CameraGrabStop();
                if (MatroxCameraList != null)
                {
                    foreach (var cam in MatroxCameraList)
                    {
                        //cam free
                        MIL.MdigFree(cam.DigitizerID);
                        try
                        {
                            //board free
                            MIL.MsysFree(cam.SystemID);
                        }
                        catch
                        {
                            //보드에 연결된 카메라가 다 닫히지 않았다면 이리로 들어옴
                        }
                    }
                    MatroxCameraList.Clear();
                    
                    //app free
                    MIL.MappFree(_MilApplication);
                }
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message + "\n" + err.StackTrace);
            }
        }

        private void CameraGrabStart()
        {
            try
            {
                //이미지 버퍼 배열 선언
                _ImageBuffer = new MIL_ID[BufferCount];

                //배열 수만큼 돎
                for(int i = 0; i < _ImageBuffer.Length; i++)
                {
                    //400x400 크기의 모노 이미지 버퍼 선언 (원하는대로 변경 가능)
                    MIL.MbufAllocColor(CurrentUseCamera.SystemID, 1, 400, 400, 8, MIL.M_IMAGE + MIL.M_GRAB + MIL.M_PROC, ref _ImageBuffer[i]);
                }
                //그랩 콜백 등록
                _GrabCallbackFunc = OnGrab;
                //콜백 및 그랩 비동기 시작
                MIL.MdigProcess(CurrentUseCamera.DigitizerID, _ImageBuffer, _ImageBuffer.Length, MIL.M_START, MIL.M_ASYNCHRONOUS + MIL.M_TRIGGER_FOR_FIRST_GRAB, _GrabCallbackFunc, IntPtr.Zero);
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message + "\n" + err.StackTrace);
            }
        }
        private void CameraGrabStop()
        {
            try
            {
                if(CurrentUseCamera != null)
                {
                    //콜백 및 그랩 비동기 종료
                    MIL.MdigProcess(CurrentUseCamera.DigitizerID, _ImageBuffer, _ImageBuffer.Length, MIL.M_STOP, MIL.M_DEFAULT, _GrabCallbackFunc, IntPtr.Zero);

                    //buffer 메모리 반환
                    foreach(var buf in _ImageBuffer)
                    {
                        MIL.MbufFree(buf);
                    }
                    _ImageBuffer = null;
                }
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message + "\n" + err.StackTrace);
            }
        }

        //비동기 그랩 콜백 메서드
        private MIL_INT OnGrab(MIL_INT eventProp, MIL_ID eventId, IntPtr userData)
        {
            try
            {
                //지금 콜백에 들어온 Image Buffer ID 가져오기
                MIL_ID currentBuf = MIL.M_NULL;
                MIL.MdigGetHookInfo(eventId, eventProp + MIL.M_BUFFER_ID, ref currentBuf);

                //버퍼 byte[] 로 복사
                byte[] rawImage = new byte[400 * 400];
                MIL.MbufGet2d(currentBuf, 0, 0, 400, 400, rawImage);
                
                //BitmapSource로 변환
                var img = BitmapSource.Create(400, 400, 96d, 96d, PixelFormats.Indexed8, BitmapPalettes.Gray256, rawImage, 400);
                if (img.CanFreeze) img.Freeze();

                DisplayBitmapSource = img;

                return 0;
            }
            catch (Exception err)
            {
                return -1;
            }
        }
        #endregion

        #endregion

    }

    public class MatroxCamera
    {
        public MIL_ID SystemID { get; set; }
        public MIL_ID DigitizerID { get; set; }
    }
}
