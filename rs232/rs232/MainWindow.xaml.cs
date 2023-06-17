using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.IO.Ports;
using System.ComponentModel;
using System.Threading;
using System.Diagnostics;
using Microsoft.Win32;
using System.IO;
using System.Windows.Forms.VisualStyles;

namespace rs232
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        /// <summary>
        /// Represents a port currently used by this instance of the program.
        /// Has in-built methods for:
        ///     - setting communication parameters (speed, flow control, etc.) - basically nothing has to be taken care of manually.
        ///     - sending and receiving a message.
        /// </summary>
        private SerialPort _localPort = new SerialPort();

        /// <summary>
        /// Decides if the ASCII send button, the binary send button and PING send button are enabled.
        /// All these send buttons are disabled when:
        ///     - a port hasn't been opened yet,
        ///     - a PING is in process,
        ///     - a transaction has begun and a transaction reply hasn't been received yet.
        /// </summary>
        public bool SendEnabled { get; set; } = false;

        /// <summary>
        /// A list of all found ports, displayed in the ComboBox.
        /// 
        /// Not all these ports need to be available - if a port has been found out to be used by an other program
        /// or another instance of this program, an error message will be written at the bottom of the application window
        /// and the port will not be opened.
        /// </summary>
        public List<string> Ports { get; set; } = new List<string>();

        /// <summary>
        /// Text that appears next to the PING button after it's clicked.
        /// Can say either "PING timed out" on timeout
        /// or "Round Trip Delay [ms]: (measured RDT value)" when a reply is succesfully received.
        /// </summary>
        public string PingText { get; set; } = "";

        /// <summary>
        /// Text inside error label at the bottom of the application window.
        /// Is empty when no errors found.
        /// </summary>
        public string ErrorText { get; set; } = "";

        /// <summary>
        /// Describes whether message headers should be displayed in the "Sent" and "Received" boxes.
        /// Header meaning:
        ///     00 - regular message
        ///     01 - PING query
        ///     02 - PING answer
        ///     03 - transaction query
        ///     04 - transaction answer
        /// </summary>
        bool showHeaders = false;

        /// <summary>
        /// Specifies if the last sent message was a PING. Used for displaying the correct info message
        /// when a PING/transaction is timed out.
        /// </summary>
        bool isPing = false;

        /// <summary>
        /// Calls the <c>TransactionTimeout</c> method when the set Timeout time elapsed with no reply
        /// after a PING/transaction is sent.
        /// </summary>
        Timer transactionTimer;

        /// <summary>
        /// Used for measuring the reply time (Round Trip Delay) during PING
        /// and for checking if the reply has been received in the proper time window (less than timeout value).
        /// </summary>
        Stopwatch transactionElapsedWaitingTime = new Stopwatch();

        /// <summary>
        /// Contains the translated to text version of content inside the basic hex editor.
        /// Displayed in the "Translated binary" box.
        /// </summary>
        public string TranslatedText { get; set; } = "";

        /// <summary>
        /// Event for updating the GUI when something should be changed (for example text content
        /// or button clickability).
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Used to separate message headers from the message content. Allows the "Show headers" checkbox to effortlessly
        /// switch between displaying headers or not.
        /// </summary>
        struct Message
        {
            /// <summary>
            /// Two first characters of the messsage.
            /// </summary>
            public string header;

            /// <summary>
            /// Rest of the message.
            /// </summary>
            public string content;

            public Message(string h, string c) : this()
            {
                this.header = h;
                this.content = c;
            }
        }

        /// <summary>
        /// List of sent messages to be displayed in the Sent box.
        /// Is emptied when "Clear" button is clicked.
        /// 
        /// Is not directly data binded to the box - its contents are used to fill the "SentText" property
        /// that is data binded to the Sent box.
        /// </summary>
        List<Message> sentMessages = new List<Message>();

        /// <summary>
        /// List of received messages to be displayed in the Received box.
        /// Is emptied when "Clear" button is clicked.
        /// 
        /// Is not directly data binded to the box - its contents are used to fill the "ReceivedText" property
        /// that is data binded to the Received box.
        /// </summary>
        List<Message> receivedMessages = new List<Message>();

        /// <summary>
        /// Text displayed in the "Sent" box.
        /// </summary>
        public string SentText { get; set; } = "";

        /// <summary>
        /// Text displayed in the "Received" box.
        /// </summary>
        public string ReceivedText { get; set; } = "";

        /// <summary>
        /// A message is only saved in the <c>receivedMessages</c> list and
        /// displayed in the Received box after the terminator is detected.
        /// 
        /// Until that happens, the message parts are combined and stored here. This value is cleared (set to "")
        /// when a terminator is found.
        /// </summary>
        StringBuilder fullReceivedMessage = new StringBuilder("");

        public MainWindow()
        {
            //Initializes the Ports list, to be displayed in the port selection ComboBox.
            RefreshPortList();

            //Set function that handles data coming in.
            _localPort.DataReceived += new SerialDataReceivedEventHandler(ReceiveMessage);

            //Initialize GUI.
            InitializeComponent();

            //Fill whole HexGrid with TextBoxes.
            InitializeHexGrid();
        }

        /// <summary>
        /// Refreshes port selection ComboBox. Called during construction of the window
        /// and when "Refresh" button is clicked.
        /// </summary>
        private void RefreshPortList()
        {
            this.Ports.Clear();

            Ports = SerialPort.GetPortNames().ToList();
            OnPropertyChanged("Ports");
        }

        /// <summary>
        /// Ugly function that directly defines GUI elements, but oh well - I'm gonna cope and say that I don't want to
        /// mess around with ListViews and ItemTemplates in a small project.
        /// 
        /// Creates a TextBox for every position in the binary HexGrid.
        /// </summary>
        private void InitializeHexGrid()
        {
            //Both loops iterate starting on 1 because ROW 0 and COLUMN 0
            //are used for row/column headers.

            for(int row = 1; row < 10; row++)       //0000h - 0080h, 9 rows
            {
                for (int col = 1; col < 17; col++)  //00 - 0F, 16 columns
                {
                    //Create TextBox and set its properties.
                    TextBox textBox = new TextBox();
                    textBox.Text = "00";    
                    textBox.Height = 20;
                    textBox.Width = 20;
                    textBox.HorizontalContentAlignment = HorizontalAlignment.Center;
                    textBox.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
                    textBox.MaxLength = 2;

                    //Method that checks if entered character is an acceptable hex character (0 - F).
                    //Does not let the character be typed in if it's something different.
                    textBox.PreviewTextInput += PreviewTextInputHandler;

                    //Updates translation when a byte is changed.
                    textBox.TextChanged += UpdateTranslationDisplay;

                    //Add TextBox at the proper position inside HexGrid.
                    this.HexGrid.Children.Add(textBox);
                    Grid.SetRow(textBox, row);
                    Grid.SetColumn(textBox, col);
                }
            }
        }

        /// <summary>
        /// Checks if text typed in a HexGrid TextBox contains only HEX characters (0 - F).
        /// Refuses the input to be entered if it's outside range.
        /// 
        /// Keep in mind that copy-paste and lower case letters don't work (because I'm lazy).
        /// </summary>
        /// <param name="sender">A TextBox inside HexGrid when user tries to type.</param>
        /// <param name="e">Response if input should be accepted or not.
        ///                 If e.Handled = false, it's accepted.
        ///                 If e.Handled = true, its' rejected. </param>
        private void PreviewTextInputHandler(Object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            string hexChars = "0123456789ABCDEF"; //Acceptable characters.

            e.Handled = false;                    //Accept input by default.

            foreach (var inputChar in e.Text)
            {
                if (!hexChars.Contains(inputChar))
                    e.Handled = true;             //If found any character outside acceptable range, don't let the input be entered.
            }
        }

        /// <summary>
        /// When any TextBox inside HexGrid changes its content, update the translation to ASCII displayed
        /// below in the "Translated binary" box.
        /// </summary>
        /// <param name="sender">TextBox inside HexGrid that has been modified.</param>
        /// <param name="e">Not relevant here because the whole translation is updated anyway.</param>
        private void UpdateTranslationDisplay(object sender, TextChangedEventArgs e)
        {
            //Get all elements from the grid (including ROW/COLUMN headers).
            var allGridElements = this.HexGrid.Children;

            //Filter out the grid elements to only TextBoxes (that contain the binary data).
            List<TextBox> hexGrids = new List<TextBox>();
            foreach (var gridElement in allGridElements)
            {
                if (gridElement is TextBox)
                    hexGrids.Add(gridElement as TextBox);
            }

            string translatedMessage = "";

            foreach (var hexGrid in hexGrids)
            {
                //Get a singular TextBox content.
                string hexInput = hexGrid.Text;

                //Translates only complete bytes
                if(hexInput.Length == 2)
                {
                    //Convert HEX value written as a string to a decimal value.
                    int decNumber = Convert.ToInt32(hexInput.Substring(0, 2), 16);

                    //Get ASCII character corresponding to the decimal value.
                    //If value above 127, it will be represented as "?", since there
                    //is no consensus about how such a value should be displayed.
                    string character = Encoding.ASCII.GetString(new byte[] { (byte)decNumber });

                    //Add the translated character to the whole message.
                    translatedMessage += character;
                }
            }

            //Update the "Translated binary" box.
            this.TranslatedText = translatedMessage;
            OnPropertyChanged(nameof(TranslatedText));
        }

        /// <summary>
        /// Updates a GUI data binding specified by the propertyName.
        /// </summary>
        /// <param name="propertyName">Which binding should be updated.</param>
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Updates port list ComboBox on "Refresh" button click.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshPortList();
        }

        /// <summary>
        /// Sets port settings according to GUI inputs.
        /// 
        /// Throws ArgumentException when an invalid value found.
        /// </summary>
        private void ReadPortSettings()
        {
            string timeoutInput = this.TimeoutTextBox.Text;
            int timeout;

            if (string.IsNullOrEmpty(timeoutInput) || !Int32.TryParse(timeoutInput, out timeout) || timeout <= 0)
                throw new ArgumentException("Invalid timeout value.");

            string terminator = GetTerminator(this.TerminatorBox.Text);

            //this._localPort.NewLine does not accept a "" terminator value
            if(terminator == "")
                throw new ArgumentException("Terminator can't be completely empty.");

            this._localPort.NewLine = terminator;
            this._localPort.PortName = this.PortBox.Text;
            this._localPort.BaudRate = Convert.ToInt32(this.SpeedBox.Text);
            this._localPort.DataBits = Convert.ToInt32(this.DataBitsBox.Text);
            this._localPort.Parity = StringToParity(this.ParityBox.Text);
            this._localPort.StopBits = StringToStopBits(this.StopBitsBox.Text);
            this._localPort.Handshake = (Handshake)this.ControlBox.SelectedIndex;

            //Timeout accepts only an Int milliseconds value.
            this._localPort.ReadTimeout = timeout;
            this._localPort.WriteTimeout = timeout;

            this._localPort.Encoding = Encoding.Default;
        }

        /// <summary>
        /// Attempts to open the port if it's possible.
        /// </summary>
        /// <returns>True if port succesfully opened.</returns>
        private void OpenPort()
        {
            //If input values invalid, do not attempt to open the port.
            try
            {
                ReadPortSettings();
            }
            catch(ArgumentException e)
            {
                this.ErrorText = e.Message;
                this.OnPropertyChanged("ErrorText");
                throw;
            }

            try
            {
                _localPort.Open();
            }
            catch (UnauthorizedAccessException)    //Fails when the selected port is already used.
            {
                this.ErrorText = "Port is already used by another program.";
                this.OnPropertyChanged("ErrorText");
                throw;
            }
        }

        /// <summary>
        /// Attempts to open the port if it's possible.
        /// </summary>
        /// <param name="sender">"Open port" button.</param>
        /// <param name="e"></param>
        private void OpenPortButton_Click(object sender, RoutedEventArgs e)
        {
            //Clear error text.
            this.ErrorText = "";
            this.OnPropertyChanged("ErrorText");

            if(this.PortBox.SelectedIndex == -1)
            {
                this.ErrorText = "COM not available.";
                this.OnPropertyChanged("ErrorText");
                return;
            }

            string a = this.PortBox.SelectedValue.ToString();

            //Fail if no COM has been selected.
            //If no COM ports exist on the computer, the ComboBox is completely empty.
            if(string.IsNullOrEmpty(a))
            {
                this.ErrorText = "COM not available.";
                this.OnPropertyChanged("ErrorText");
                return;
            }

            //Fail if port is already open. Try to close the port.
            //Generally happens when physical cables are messed with when the program is running.
            if (_localPort.IsOpen)
            {
                this.ErrorText = "Waiting for port to close...";
                this.OnPropertyChanged("ErrorText");
                this.ClosePort();
                return;
            }

            try
            {
                OpenPort(); 
            }
            catch(Exception ex)
            {
                return;
            }

            //Disable OpenPort button, enable Close Port button, enable all Send buttons.

            this.OpenPortButton.IsEnabled = false;
            this.SendEnabled = true;

            OnPropertyChanged("SendEnabled");
            this.ClosePortButton.IsEnabled = true;
        }

        
        /// <summary>
        /// Closes port along with updating button clickability.
        /// </summary>
        private void ClosePort()
        {
            //Enable OpenPort button, disable Close Port button, disable all Send buttons.

            this.ClosePortButton.IsEnabled = false;
            _localPort.DiscardOutBuffer();
            _localPort.DiscardInBuffer();
            _localPort.Close();
            this.OpenPortButton.IsEnabled = true;

            this.SendEnabled = false;

            OnPropertyChanged("SendEnabled");
        }

        private void ClosePortButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_localPort.IsOpen)
            {
                this.ErrorText = "Waiting for port to open...";
                this.OnPropertyChanged("ErrorText");
                return;
            }

            ClosePort();
        }

        private Parity StringToParity(string str)
        {
            switch (str)
            {
                case "Odd":
                    return Parity.Odd;
                case "Even":
                    return Parity.Even;
                case "None":
                    return Parity.None;
                default:
                    throw new ArgumentException("Not a valid parity type.");
            }
        }

        private StopBits StringToStopBits(string str)
        {
            switch (str)
            {
                case "1":
                    return StopBits.One;
                case "2":
                    return StopBits.Two;
                default:
                    throw new ArgumentException("Not a valid stop bits value.");
            }
        }

        private string GetTerminator(string str)
        {
            switch(str)
            {
                case "None":
                    return " "; //Can't be set to ""
                case "CR":
                    return "\r";
                case "LF":
                    return "\n";
                case "CRLF":
                    return "\r\n";
                case "Custom":
                    string customTerminator = "";
                    customTerminator += (!String.IsNullOrEmpty(this.Terminator1Box.Text)) ? this.Terminator1Box.Text : "";
                    customTerminator += (!String.IsNullOrEmpty(this.Terminator2Box.Text)) ? this.Terminator2Box.Text : "";
                    return customTerminator;
                default:
                    throw new ArgumentException("Not a valid terminator value.");
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            //Temporarily lock message box from editing.
            this.NewMessageBox.IsReadOnly = true;

            //Reset error message
            this.ErrorText = "";
            this.OnPropertyChanged("ErrorText");

            //Check if port is open
            if (_localPort == null)
                return;

            if (!_localPort.IsOpen)
            {
                this.NewMessageBox.IsReadOnly = false;
                this.ErrorText = "Port not open";
                this.OnPropertyChanged("ErrorText");
                return;
            }

            //SET HEADER ACCORDING TO MESSAGE TYPE AND
            //OPTIONALLY DO OTHER ACTIONS BEFORE SENDING THE MESSAGE.

            string messageHeader = "00";                            //BY DEFAULT - REGULAR MESSAGE

            if((bool)this.TransactionCheckBox.IsChecked)            //If it's actually a TRANSACTION QUERY.
            {
                ActivateTransaction();
                isPing = false;

                messageHeader = "03";   //Set header to TRANSACTION QUERY header.
            }
            else if((bool) this.TransactionReplyCheckBox.IsChecked) //If it's actually a TRANSACTION REPLY.
            {
                messageHeader = "04";   //Set header to TRANSACTION REPLY header.
            }

            SendMessageAndUpdateDisplay(text: this.NewMessageBox.Text,
                                        header: messageHeader,
                                        addDate: (bool)this.DateTimeCheckBox.IsChecked);
        }

        private void SendMessageAndUpdateDisplay(string text, string header, bool addDate)
        {
            //Send the message.
            string sentMessage = SendMessage(text, header, addDate);

            //Reset and activate message box
            this.NewMessageBox.Text = "";
            this.NewMessageBox.IsReadOnly = false;

            //Save message to sentMessages list.
            Message newSentMessage = new Message(header, sentMessage.Substring(2));
            this.sentMessages.Add(newSentMessage);
            this.RefreshListDisplay();
        }

        //Builds and sends the message.
        //00 - normal
        //01 - ping, 02 - ping response
        //03 - transaction begin, 04 - transaction end
        private string SendMessage(string text, string messageType, bool sendTime)
        {
            string message = CreateMessage(text, messageType, sendTime);
            _localPort.DiscardOutBuffer();
            _localPort.DiscardInBuffer();
            _localPort.Write(message);
            return message;
        }

        //Builds the full message contents.
        private string CreateMessage(string text, string messageType, bool sendTime)
        {
            var message = new StringBuilder();
            message.Append(messageType);                    //Header

            if (sendTime)
                message.Append(DateTime.Now.ToString("G")); //Optional date-time

            message.Append(text);                           //Content
            message.Append(this._localPort.NewLine);        //Terminator
            return message.ToString();
        }

        private void ReceiveMessage(object sender, SerialDataReceivedEventArgs e)
        {
            string latestMessage = _localPort.ReadExisting();
            fullReceivedMessage.Append(latestMessage);

            string fullMessageString = fullReceivedMessage.ToString();

            if (fullMessageString.EndsWith(_localPort.NewLine))     //READ FULL MESSAGE WHEN TERMINATOR FOUND
            {
                //UPDATE "RECEIVED" BOX DISPLAY
                string header = fullMessageString.Substring(0, 2);
                string content = fullMessageString.Substring(2);

                Message receivedMessage = new Message(header, content);
                this.receivedMessages.Add(receivedMessage);
                RefreshListDisplay();

                //If PING QUERY - send PING REPLY back
                if(header == "01")
                {
                    SendMessage(DateTime.Now.ToString("G") + "PING REPLY", "02", false);
                }
                else if(header == "02")     //If PING REPLY - end "transaction" and display
                {
                    if(this.transactionElapsedWaitingTime.Elapsed.TotalMilliseconds < this._localPort.ReadTimeout)
                    {
                        this.EndTransaction();

                        this.PingText = "Round Trip Delay [ms]: " + transactionElapsedWaitingTime.Elapsed.TotalMilliseconds;
                        OnPropertyChanged(nameof(this.PingText));
                        this.transactionElapsedWaitingTime.Reset();
                    }
                }
                else if(header == "04")     //If TRANSACTION REPLY - end transaction.
                {
                    if (this.transactionElapsedWaitingTime.Elapsed.TotalMilliseconds < this._localPort.ReadTimeout)
                    {
                        this.EndTransaction();
                        this.transactionElapsedWaitingTime.Reset();
                    }
                }

                fullReceivedMessage.Clear();
            }
        }

        /// <summary>
        /// Clears lists and Sent/Received boxes content.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            this.ReceivedBox.Text = "";
            this.SentBox.Text = "";

            this.receivedMessages.Clear();
            this.sentMessages.Clear();
        }

        private void RefreshListDisplay()
        {
            string sentMessages = "";

            foreach(var message in this.sentMessages)
            {
                if (showHeaders)
                    sentMessages += message.header;

                sentMessages += message.content;
            }
            this.SentText = sentMessages;

            string receivedMessages = "";
            foreach (var message in this.receivedMessages)
            {
                if (showHeaders)
                    receivedMessages += message.header;

                receivedMessages += message.content;
            }
            this.ReceivedText = receivedMessages;

            OnPropertyChanged("SentText");
            OnPropertyChanged("ReceivedText");
        }

        private void ShowHeaderCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            showHeaders = true;
            RefreshListDisplay();
        }

        private void ShowHeaderCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            showHeaders = false;
            RefreshListDisplay();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (this._localPort.IsOpen)
                ClosePort();
        }

        private void TransactionTimeout(object? state)
        {
            if (isPing)
                this.PingText = "PING timed out.";
            else
                this.ErrorText = "Transaction timed out.";

            this.OnPropertyChanged("PingText");
            this.OnPropertyChanged("ErrorText");
            this.transactionElapsedWaitingTime.Reset();

            EndTransaction();
        }

        private void ActivateTransaction()
        {
            this.ErrorText = "";
            OnPropertyChanged("ErrorText");

            this.transactionTimer = new Timer(TransactionTimeout, null, this._localPort.ReadTimeout, Timeout.Infinite);

            this.SendEnabled = false;

            OnPropertyChanged("SendEnabled");

            this.transactionElapsedWaitingTime.Start();
        }

        private void EndTransaction()
        {
            this.SendEnabled = true;

            OnPropertyChanged("SendEnabled");

            this.transactionElapsedWaitingTime.Stop();
            this.transactionTimer.Dispose();
            isPing = false;
        }

        private void PingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_localPort.IsOpen)
                return;

            ActivateTransaction();
            isPing = true;

            string mes = DateTime.Now.ToString("G") + "PING";

            SendMessage(mes, "01", false);

            Message newSentMessage = new Message("01", mes + this._localPort.NewLine);
            this.sentMessages.Add(newSentMessage);
            this.RefreshListDisplay();
        }

        private void TransactionCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            TransactionReplyCheckBox.IsChecked = false;
        }

        private void TransactionReplyCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            TransactionCheckBox.IsChecked = false;
        }

        private void SendBinaryButton_Click(object sender, RoutedEventArgs e)
        {
            var allGridElements = this.HexGrid.Children;
            List<TextBox> hexGrids = new List<TextBox>();

            foreach (var gridElement in allGridElements)
            {
                if (gridElement is TextBox)
                    hexGrids.Add(gridElement as TextBox);
            }

            int indexOfLastByte = -1;
            for (int i = 0; i < hexGrids.Count; i++)
            {
                string hexByte = hexGrids[i].Text;

                if (hexByte.Length != 2)
                {
                    this.ErrorText = "Incomplete byte.";
                    OnPropertyChanged("ErrorText");
                    return;
                }

                if (hexByte != "00")
                    indexOfLastByte = i;
            }

            if (indexOfLastByte == -1)
            {
                this.ErrorText = "All bytes are 0.";
                OnPropertyChanged("ErrorText");
                return;
            }

            string message = "";
            for (int i = 0; i <= indexOfLastByte; i++)
            {
                message += hexGrids[i].Text;
            }

            for (int i = 0; i < hexGrids.Count; i++)
            {
                hexGrids[i].Text = "00";
            }

            SendMessageAndUpdateDisplay(message, "00", false);
        }

        private void LoadFileButton_Click(object sender, RoutedEventArgs e)
        {
            this.ErrorText = "";
            OnPropertyChanged("ErrorText");

            OpenFileDialog openFileDialog = new OpenFileDialog();

            byte[] fileContents;
            if (openFileDialog.ShowDialog() == true)
            {
                fileContents = File.ReadAllBytes(openFileDialog.FileName);

                if (fileContents.Length > 144)
                {
                    this.ErrorText = "File too long";
                    OnPropertyChanged("ErrorText");
                    return;
                }

                var allGridElements = this.HexGrid.Children;
                List<TextBox> hexGrids = new List<TextBox>();

                foreach (var gridElement in allGridElements)
                {
                    if (gridElement is TextBox)
                        hexGrids.Add(gridElement as TextBox);
                }

                for (int i = 0; i < hexGrids.Count; i++)
                {
                    hexGrids[i].Text = "00";
                }

                string hexString = Convert.ToHexString(fileContents);
                for (int i = 0; i < fileContents.Length; i++)
                {
                    hexGrids[i].Text = hexString.Substring(0, 2);
                    hexString = hexString.Substring(2);
                }

                
            }
        }


    }
}
