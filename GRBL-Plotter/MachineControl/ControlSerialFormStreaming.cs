﻿/*  GRBL-Plotter. Another GCode sender for GRBL.
    This file is part of the GRBL-Plotter application.
   
    Copyright (C) 2015-2021 Sven Hasemann contact: svenhb@web.de

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
/*  Many thanks to:
    3dpBurner Sender. A GCODE sender for GRBL based devices.
    This file is part of 3dpBurner Sender application.   
    Copyright (C) 2014-2015  Adrian V. J. (villamany) contact: villamany@gmail.com

    This project was my starting point
*/
/* 2020-09-18 split file
 * 2020-12-18 fix OnRaiseStreamEvent %
 * 2021-01-23 add trgEvent to "sendStreamEvent" in time with the status query
 * 2021-01-26 line 270 make delay between probe exchange scripts variable
 * 2021-02-19 insert variables in subroutine already in startStreaming()
 * 2021-02-28 preProcessStreaming lock copy-routine and replace "requestSend()" by sendBuffer.Add
 */

// OnRaiseStreamEvent(new StreamEventArgs((int)lineNr, codeFinish, buffFinish, status));
// OnRaisePosEvent(new PosEventArgs(posWork, posMachine, grblStateNow, machineState, mParserState, rxString));// lastCmd));

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.IO;

namespace GRBL_Plotter
{
    public partial class ControlSerialForm : Form        // Form can be loaded twice!!! COM1, COM2
    {
        /************************************************************************************************************
         * Streaming data to grbl with the possibility to interrupt (pause) and sending other code (manual tool change)
         * 1. startStreaming() copy and filter gcode to streamingBuffer
         * 2. preProcessStreaming() to copy data to sendBuffer for sending if grblBuffer (grblBufferFree) has enough space
         ************************************************************************************************************/
        private bool allowStreamingEvent = true;
        private CodeBuffer streamingBuffer = new CodeBuffer();

        private bool isStreaming = false;        		// true when steaming is in progress
        private bool isStreamingRequestPause = false; 	// true when request pause (wait for idle to switch to pause)
        private bool isStreamingPause = false;    		// true when steaming-pause 
        private bool isStreamingCheck = false;    		// true when steaming is in progress (check)
        private bool isStreamingRequestStopp = false;   // 
        private bool getParserState = false;      		// true to send $G after status switched to idle
        private bool isDataProcessing = false;      		// false when no data processing pending
        private grblStreaming streamingStateNow = grblStreaming.ok;
        private grblStreaming streamingStateOld = grblStreaming.ok;
        private int lineStreamingPause = 0;
        private bool trgEvent = false;
        private bool skipM30 = false;

        private Dictionary<int, List<string>> subroutines = new Dictionary<int, List<string>>();

        public event EventHandler<StreamEventArgs> RaiseStreamEvent;
        protected virtual void OnRaiseStreamEvent(StreamEventArgs e)
        { RaiseStreamEvent?.Invoke(this, e); }

        private string listInfoStream()
        { return string.Format("{0}strmBuffer snt:{1,3}  cnfrmnd:{2,3}  cnt:{3,3}  BFree:{4,3}  lineNr:{5}  code:'{6}' state:{7}", "", streamingBuffer.IndexSent, streamingBuffer.IndexConfirmed, streamingBuffer.Count, grblBufferFree, streamingBuffer.GetConfirmedLineNr(), streamingBuffer.GetConfirmedLine(), grblStateNow.ToString()); }

/****************************************************************************
*  startStreaming called by main-Prog
*  get complete GCode list and copy to streamingBuffer
*  initialize streaming
*  if startAtLine > 0 start with pause
****************************************************************************/
        public void startStreaming(IList<string> gCodeList, int startAtLine, bool check = false)
        {
            grblCharacterCounting = Properties.Settings.Default.grblStreamingProtocol1 && !isMarlin;
            Logger.Info("Ser:{0} startStreaming at line:{1} ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼", iamSerial, startAtLine);
//            if (logEnable) LogPos.Info("(----- Start streaming {0} -----)", getTimeStampString());
            if (grblCharacterCounting)
                Logger.Info("Streaming Protocol: Character-Counting");
            else
                Logger.Info("Streaming Protocol: Simple Send-Response");

            updateLogging();

            grblBufferSize = grbl.RX_BUFFER_SIZE;  //rx bufer size of grbl on arduino 127
            grblBufferFree = grbl.RX_BUFFER_SIZE;
            Logger.Info("Set Buffer Free:{0}  Size:{1}", grblBufferFree, grblBufferSize);

            if (Properties.Settings.Default.grblPollIntervalReduce)
            { timerSerial.Interval = grbl.pollInterval * 2; }
            else
            { timerSerial.Interval = grbl.pollInterval; }
            countMissingStatusReport = (int)(10000 / timerSerial.Interval);
            Logger.Info("Timer interval:{0}  {1}", timerSerial.Interval, countMissingStatusReport);

            skipM30 = false;
            lastError = "";
            countGrblError = 0;
            lastSentToCOM.Clear();
            toolTable.init();       // fill structure
            rtbLog.Clear();

            // check if other serial are still alive
            if (useSerial2)
            {   try
                {   if (!_serial_form2.serialPortOpen)
                    { addToLog("[2nd serial port is not open]"); useSerial2 = false; }
                }
                catch
                { useSerial2 = false; }
            }
            if (useSerial3)
            {   try
                {   if (!_serial_form3.serialPortOpen)
                    { addToLog("[3rd serial port is not open]"); useSerial3 = false; }
                }
                catch
                { useSerial3 = false; }
            }

            if (!check)
            {   addToLog("[Start streaming - no echo]");
                if (useSerial2) addToLog("[Use serial 2]");
                if (useSerial3) addToLog("[Use serial 3]");
            }
            else
                addToLog("[Start code check]");
            saveLastPos();
            if (replaceFeedRate)
                addToLog("!!! Override Feed Rate");
            if (replaceSpindleSpeed)
                addToLog("!!! Override Spindle Speed");
            isStreamingPause = false;
            isStreamingRequestPause = false;
            isStreamingCheck = check;
            streamingStateNow = grblStreaming.ok;
            lineStreamingPause = -1;                // save line were M0 appeared for main GUI to show notification
            streamingBuffer.Clear();    // = new List<string>();
            resetStreaming();           // startStreaming
            if (isStreamingCheck)
            {   sendLine("$C");         // startStreaming check
                grblBufferSize = 100;   // reduce size to avoid fake errors
            }
            countLoggerUpdate = (int)(10000 / timerSerial.Interval);

            timerSerial.Stop();

            /***** collect subroutines, without resolving variables *****/
            #region subroutines
            subroutines = new Dictionary<int, List<string>>();
            string tmp;
            string[] gCode = gCodeList.ToArray<string>();
            bool prg_end = false;
            for (int i = startAtLine; i < gCode.Length; i++)
            {
                if (gCode[i].Contains("M30"))
                { prg_end = true; continue; }
                if (!prg_end)
                    continue;
                if (gCode[i].Contains("O"))
                {
                    int cmdONr = gcode.getIntGCode('O', gCode[i]);
                    if (cmdONr <= 0)
                        continue;
                    subroutines.Add(cmdONr, new List<string>());
                    Logger.Trace("Add subroutine O{0}", cmdONr);
                    for (int k = i + 1; k < gCode.Length; k++)
                    {   if (gCode[k].IndexOf("M99") >= 0)
                        { break; }
                        if (gCode[k].IndexOf("M98") >= 0)
                        {
                            double pWord = findDouble("P", -1, gCode[k]);
                            double lWord = findDouble("L", 1, gCode[k]);

                            if (subroutines.ContainsKey((int)pWord))
                            {
                                for (int repeat = 0; repeat < lWord; repeat++)
                                {
                                    foreach (string subroutineLine in subroutines[(int)pWord])          // copy subroutine
                                    {
                                        subroutines[cmdONr].Add(subroutineLine);
                                        Logger.Trace(" sub in sub {0}", subroutineLine);
                                    }
                                }
                            }
                            else
                                Logger.Error("Start stresaming Subroutine {0} not found",pWord);
                        }
                        else
                        {
                            subroutines[cmdONr].Add(gCode[k]);
                            Logger.Trace(" {0}", gCode[k]);
                        }
                    }
                }
            }
            /****************************************************************************/
#endregion
            lock (sendDataLock)
            {
                //string[] gCode = gCodeList.ToArray<string>();
                //string tmp;
                double pWord, lWord, oWord;
                string subline;
                bool tmpToolInSpindle = toolInSpindle;
                int cmdTNr = -1;
                bool foundM30 = false;
                for (int i = startAtLine; i < gCode.Length; i++)
                {
                    tmp = cleanUpCodeLine(gCode[i]);
                    if ((!string.IsNullOrEmpty(tmp)) && (tmp[0] != ';'))//trim lines and remove all empty lines and comment lines
                    {
                        int cmdMNr = gcode.getIntGCode('M', tmp);
                        int cmdGNr = gcode.getIntGCode('G', tmp);
                        cmdTNr = gcode.getIntGCode('T', tmp);

                        /***** Subroutine? ********************************************************/
                        #region subroutine
                        //                    if (tmp.IndexOf("M98") >= 0)    // any subroutines?
                        if (cmdMNr == 98)
                        {
                            pWord = findDouble("P", -1, tmp);
                            lWord = findDouble("L", 1, tmp);

                            if (subroutines.ContainsKey((int)pWord))
                            {
                                for (int repeat = 0; repeat < lWord; repeat++)
                                {
                                    foreach(string subroutineLine in subroutines[(int)pWord])          // copy subroutine
                                    {
                                        if (subroutineLine.Contains('#'))                    // check if variable neededs to be replaced
                                            streamingBuffer.Add(insertVariable(subroutineLine),i); 
                                        else 
                                            streamingBuffer.Add(subroutineLine, i);             // add gcode line to list to send 
                                    }
                                }
                            }
                            else
                                Logger.Error("Subroutine {0} not found", pWord);


                        }
                        #endregion
                        /***** Subroutine ********************************************************/
                        else
                        {
                            if (grbl.unknownG.Contains(cmdGNr))
                            {   streamingBuffer.SetSentLine("(" + tmp + " - unknown)");    // don't pass unkown GCode to GRBL because is unknown
                                tmp = streamingBuffer.GetSentLine();
                                streamingBuffer.LineWasReceived();
                                addToLog(tmp);
                            }
                            if (cmdTNr >= 0)                                        // T-word is allowed by grbl - no need to filter
                            {   setToolChangeCoordinates(cmdTNr, tmp);              // update variables e.g. "gcodeVariable[TOAX]" from tool-table
                            }
                            if (cmdMNr == 6)                                        // M06 is not allowed - remove
                            {   if (Properties.Settings.Default.ctrlToolChange)
                                {   insertToolChangeCode(i, ref tmpToolInSpindle);  // insert external script-code and insert variables 
                                    tmp = "(" + tmp + ")";
                                }
                            }
                            if (cmdMNr == 30)
                            {   if (skipM30)
                                { tmp = "(" + tmp + ")"; }
                            }

                            streamingBuffer.Add(tmp, i);        // add gcode line to list to send

                            if (cmdMNr == 30)
                            {   foundM30 = true;
                                break;
                            }
                        }
                    }
                }
                if (!foundM30)
                {   if (!skipM30)
                        streamingBuffer.Add("M30", gCode.Length - 1);    // add end
                }
                streamingBuffer.Add("($END)", gCode.Length - 1);        // add gcode line to list to send
                streamingBuffer.Add("()", gCode.Length - 1);            // add gcode line to list to send
            }   // lock
            timerSerial.Start();

            if (logEnable)
            {
                string startText = string.Format("( {0} )\r\n", getTimeStampString());
                File.WriteAllText(Application.StartupPath  + "\\logSendBuffer.nc", startText); // clear file
                File.WriteAllText(Application.StartupPath  + "\\logStreamingBuffer.nc", startText); // clear file
                File.AppendAllLines(Application.StartupPath + "\\logStreamingBuffer.nc", streamingBuffer.Buffer);
            }
            isStreaming = true;
            updateControls();
            if (startAtLine > 0)
            {   pauseStreaming();
                isStreamingPause = true;
            }
            else
            {   waitForIdle = false;
                preProcessStreaming();  // 411 
            }
        }   // startStreaming
    
        private void insertToolChangeCode(int line, ref bool inSpindle)
        {
            streamingBuffer.Add("($TS)", line);         // keyword for receiving-buffer (sendBuffer.GetConfirmedLine();) "Tool change start"
            if (inSpindle)
            {   addCodeFromFile(Properties.Settings.Default.ctrlToolScriptPut, line);
                inSpindle = false;
                streamingBuffer.Add("($TO T"+ gcodeVariable["TOLN"]+")", line);     // keyword for receiving-buffer "Tool removed"
            }
            if (!Properties.Settings.Default.ctrlToolChangeEmpty || (gcodeVariable["TOAN"] != (int)Properties.Settings.Default.ctrlToolChangeEmptyNr))
            {   addCodeFromFile(Properties.Settings.Default.ctrlToolScriptSelect, line);
                addCodeFromFile(Properties.Settings.Default.ctrlToolScriptGet, line);
                inSpindle = true;
                streamingBuffer.Add("($TI T" + gcodeVariable["TOAN"] + ")", line);  // keyword for receiving-buffer "Tool inserted"
                addCodeFromFile(Properties.Settings.Default.ctrlToolScriptProbe, line);
            }

            streamingBuffer.Add("($TE)", line);         // keyword for receiving-buffer "Tool change finished"

            if (Properties.Settings.Default.ctrlToolScriptDelay > 0)
                streamingBuffer.Add(string.Format("G4 P{0:0.00}",Properties.Settings.Default.ctrlToolScriptDelay), line);

            // save actual tool info as last tool info
            gcodeVariable["TOLN"] = gcodeVariable["TOAN"];
            gcodeVariable["TOLX"] = gcodeVariable["TOAX"];
            gcodeVariable["TOLY"] = gcodeVariable["TOAY"];
            gcodeVariable["TOLZ"] = gcodeVariable["TOAZ"];
            gcodeVariable["TOLA"] = gcodeVariable["TOAA"];
        } 

		private void addCodeFromFile(string file, int linenr)
		{
			if (File.Exists(file))
			{
				string fileCmd = File.ReadAllText(file);
				string[] commands = fileCmd.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
				string tmp;
				foreach (string cmd in commands)
				{
					tmp = cleanUpCodeLine(cmd);         // remove comments
                    if (tmp.IndexOf("M98") >= 0)
                    {
                        double pWord = findDouble("P", -1, tmp);
                        double lWord = findDouble("L", 1, tmp);

                        if (subroutines.ContainsKey((int)pWord))
                        {
                            for (int repeat = 0; repeat < lWord; repeat++)
                            {
                                foreach (string subroutineLine in subroutines[(int)pWord])          // copy subroutine
                                {
                                    if (subroutineLine.Contains('#'))                    // check if variable neededs to be replaced
                                        streamingBuffer.Add(insertVariable(subroutineLine), linenr);
                                    else
                                        streamingBuffer.Add(subroutineLine, linenr);             // add gcode line to list to send 
                                }
                            }
                        }
                        else
                            Logger.Error("addCodeFromFile Subroutine {0} not found", pWord);

                    }
                    else
                    {
                        if (tmp.Contains('#'))              // check if variable neededs to be replaced                
                        { tmp = insertVariable(tmp); }
                        if (tmp.Length > 0)
                        { streamingBuffer.Add(tmp, linenr); }
                    }
				}
			}
		}


    /****** get number from string ******/
		private static double findDouble(string start, double notfound, string txt)
        {   int istart = txt.IndexOf(start);
            if (istart < 0)
                return notfound;
            string line = txt.Substring(istart+start.Length);
            string num = "";
            foreach (char c in line)
            {
                if (Char.IsLetter(c))
                    break; 
                else if (Char.IsNumber(c) || c == '.' || c == '-')
                    num += c;
            }
            if (num.Length<1)
                return notfound;
            return double.Parse(num, System.Globalization.NumberFormatInfo.InvariantInfo);
        }
		
/************************************************************************
 * pauseStreaming()
 * Pause or restart streaming after button click
 ************************************************************************/ 
        public void pauseStreaming()
        {
			updateLogging();
            if (logStartStop) Logger.Trace("Ser:{0} pauseStreaming()  isStreamingPause {1}   isStreamingRequestPause {2}   {3}", iamSerial, isStreamingPause, isStreamingRequestPause, listInfoStream());
// start pause			
            if (!isStreamingPause)
            {   isStreamingRequestPause = true;     // wait until buffer is empty before switch to pause
                addToLog("[Pause streaming - wait for IDLE]");
                addToLog("[Save Settings]");
                Logger.Info("pauseStreaming RequestPause ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲");
                streamingStateNow = grblStreaming.waitidle;
                getParserState = true;
            }
// restart streaming			
            else
            {   //if ((posPause.X != posWork.X) || (posPause.Y != posWork.Y) || (posPause.Z != posWork.Z))
                addToLog("++++++++++++++++++++++++++++++++++++");
                timerSerial.Interval = grbl.pollInterval;
                if (parserStateGC.Contains("F0"))
                {   parserStateGC = parserStateGC.Replace("F0", "F100");            // Avoid missing feed rate
                    addToLog(string.Format("[Fix F0: {0}]", parserStateGC));
                }
							
                if (!xyzPoint.AlmostEqual(posPause, posWork))	// restore position
                {
                    if (logStartStop) Logger.Trace("AlmostEqual posPause X{0:0.000} Y{1:0.000}  posWork X{2:0.000} Y{3:0.000}", posPause.X, posPause.Y, posWork.X, posWork.Y);
                    addToLog("[Restore Position]");
                    requestSend(string.Format("G90 G0 X{0:0.000} Y{1:0.000}", posPause.X, posPause.Y).Replace(',', '.'));  // restore last position
                    if (logStartStop) Logger.Trace("[Restore] X{0:0.000} Y{1:0.000}  State:{2}", posPause.X, posPause.Y, parserStateGC);
					requestSend("G4 P0.5");       // wait 1 second // changed from 1 to 0,5 2021-01-26
                    requestSend(string.Format("G0 Z{0:0.000}", posPause.Z).Replace(',', '.'));                      // restore last position
                }
				
                addToLog("[Start streaming - no echo]");
                addToLog("[Restore Settings: "+ parserStateGC+" ]");
                Logger.Info("pauseStreaming start streaming ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼");
                streamingStateNow = grblStreaming.ok;
                lineStreamingPause = -1;				// save line were M0 appeared for main GUI to show notification

                string parserStateGC_wo_M = "";
                if (parserStateGC.Contains("M"))
                    parserStateGC.Substring(parserStateGC.IndexOf("M") - 1);
				if (parserStateGC.Length > 0)
				{
                    if (isGrblVers0)
					{   addToLog("[Restore Settings woM: " + parserStateGC_wo_M + " ]");
						requestSend(parserStateGC_wo_M);           
					}
					else
					{	addToLog("[Restore Settings: " + parserStateGC + " ]");
						requestSend(parserStateGC);          
					}
				}

                waitForIdle = false;
                waitForOk = false;
                isStreamingPause = false;
                isStreamingRequestPause = false;

                preProcessStreaming();
 //               processSend();
            }
            updateControls();
        }   // pauseStreaming

/******************************************************************
 * stopStreaming()
 * Initiate stop (wait until IDLE)
 ******************************************************************/
        public void stopStreaming(bool isNotStartup = true)
        {
            if (isStreamingCheck)
            {   sendLine("$C");         // stopStreaming check
                isStreamingCheck = false;
            }

            if (isNotStartup)
                stopStreamingFinal();
            else
            {
                isHeightProbing = false;
                resetStreaming(false);      // stopStreaming
                isStreamingRequestStopp = true;         // 20200717
                isStreamingRequestPause = true;     // 20200717
                Logger.Info(" stopStreaming() - wait for IDLE - sent:{0}  received:{1}  ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲", streamingBuffer.IndexSent, streamingBuffer.IndexConfirmed);
            }
        }
		
/******************************************************************
 * stopStreamingFinal()
 * Finally stop 
 ******************************************************************/
        public void stopStreamingFinal()
        {						
            int line = 0;
            line = streamingBuffer.GetSentLineNr();
            if (logStartStop) Logger.Trace(" stopStreamingFinal() gCodeLinesSent {0}  gCodeLineNr.Count {1}", streamingBuffer.IndexSent, streamingBuffer.Count);
            sendStreamEvent(grblStreaming.stop);        // stopStreamingFinal

            isHeightProbing = false;
			
            resetStreaming(true);       // stopStreamingFinal()
            if (logStartStop) Logger.Trace(" stopStreamingFinal() - lines in buffer {0}", (streamingBuffer.IndexSent - streamingBuffer.IndexConfirmed));

            if (isStreamingCheck)
            {   sendLine("$C");         // stopStreamingFinal check
                isStreamingCheck = false;
            }
            updateControls();
            if (Properties.Settings.Default.grblPollIntervalReduce)
            {   timerSerial.Interval = grbl.pollInterval;
                countMissingStatusReport = (int)(10000 / timerSerial.Interval);
            }
        }

/***** reset streaming variables *****/
        private void resetStreaming(bool isStopping = true)
        {
            externalProbe = false;
            isStreaming = false;
            isStreamingRequestPause = false;
            isStreamingPause = false;
			isStreamingRequestStopp = false;
			
			if (isStopping)		// 20200717
            {	streamingBuffer.Clear();
                sendBuffer.Clear();
                grblBufferFree = grblBufferSize;
            }
            grbl.lastMessage = "";
            grbl.lastErrorNr = 0;
        }

/********************************************************
 * processOkStreaming()
 * process ok message for streaming
 * called in RX event chain, from processGrblMessages()
 ********************************************************/
        public void processOkStreaming()
        {
            if (isStreaming)    // || isStreamingRequestStopp)
            {
// confirm sent data
                if (!isStreamingPause)
                {   streamingBuffer.LineWasReceived();
    //                streamingBuffer.DeleteLine();		// better don't removeAt(0)...
                    allowStreamingEvent = true;
                }
                else
                    streamingStateNow = grblStreaming.pause;   // update status
				
// send status via event to main GUI
                if ((streamingStateOld != streamingStateNow) || allowStreamingEvent)
                {
                    if (streamingStateNow != grblStreaming.pause)
                    { if (trgEvent) sendStreamEvent(streamingStateNow); trgEvent = false; }  // streaming processOkStreaming
                    streamingStateOld = streamingStateNow;      //grblStatus = oldStatus;
                    allowStreamingEvent = false;
                }
            }
        }

/*****  sendStreamEvent update main prog  *****/
        private void sendStreamEvent(grblStreaming status, int linePause=-1)
        {
			int lineNrSent = streamingBuffer.GetSentLineNr();
            if (linePause > 0)
                lineNrSent = linePause;

            int lineNrConfirmed = streamingBuffer.GetConfirmedLineNr();
            if (status == grblStreaming.error)
                lineNrConfirmed = sendBuffer.GetConfirmedLineNr() +1;

            // progressbar.value is int
            int codeFinish = 0;
			if (streamingBuffer.Count != 0)
				codeFinish = (int)Math.Ceiling((float)lineNrConfirmed * 100 / streamingBuffer.MaxLineNr) +1;      // to reach 100%
            int buffFinish = 0;
			if (grblBufferSize != 0)
				buffFinish = (int)Math.Ceiling((float)(grblBufferSize - grblBufferFree) * 100 / grblBufferSize) +1; // to reach 100%

            if (codeFinish > 100) { codeFinish = 100; }
            if (buffFinish > 100) { buffFinish = 100; }

//            Logger.Trace("OnRaiseStreamEvent {0} {1} {2} {3} {4}", lineNrSent, lineNrConfirmed, codeFinish, buffFinish, status);
            OnRaiseStreamEvent(new StreamEventArgs(lineNrSent, lineNrConfirmed, codeFinish, buffFinish, status));
        }

/**********************************************************************************  
 *  preProcessStreaming copy line by line (requestSend(line)) to sendBuffer until 
 *  grbl-buffer (grblBufferFree) is filled, M0 or M30 or buffer-end reached.
 *  Insert script-code on tool change.
 *  requestSend -> processSend -> sendLine  -  dec. grblBufferFree
 *  called in startStreaming, pauseStreaming (to restart) and timer
 **********************************************************************************/
        private void preProcessStreaming()
        {
            int lengthToSend = streamingBuffer.LengthSent() + 1;

            if (waitForIdle || isStreamingRequestPause || (countPreventIdle > 0))
            {   return;    }

            while ((streamingBuffer.IndexSent < streamingBuffer.Count) && (grblBufferFree >= lengthToSend) && !waitForIdle && (streamingStateNow != grblStreaming.pause) && externalCOMReady())
            {
                lock (sendDataLock)
                {
                    string line = streamingBuffer.GetSentLine();
                    if ((line == "OV") || (line == "UR"))
                    {   Logger.Error("preProcessStreaming read:{0} from  streamingBuffer index:{1} count:{2}",line, streamingBuffer.IndexSent, streamingBuffer.Count);
                        break;
                    }
                    streamingStateNow = grblStreaming.ok;       // default status

                    int cmdMNr = gcode.getIntGCode('M', line);

                    if ((replaceFeedRate) && (gcode.getStringValue('F', line) != ""))
                    {
                        string old_value = gcode.getStringValue('F', line);
                        replaceFeedRateCmdOld = old_value;
                        line = line.Replace(old_value, replaceFeedRateCmd);
                        streamingBuffer.SetSentLine(line);
                    }

                    if ((replaceSpindleSpeed) && (gcode.getStringValue('S', line) != ""))
                    {
                        string old_value = gcode.getStringValue('S', line);
                        line = line.Replace(old_value, replaceSpindleSpeedCmd);
                        replaceSpindleSpeedCmdOld = old_value;
                        streamingBuffer.SetSentLine(line);
                    }

                    #region M0  // Program pause
                    if ((cmdMNr == 0) && !isStreamingCheck) // M0 request pause
                    {
                        if (!Properties.Settings.Default.guiDisableProgramPause)
                        {
                            if (logStartStop || logReceive) Logger.Trace("[Pause streaming - skip M0 - wait for IDLE]  indx-sent:{0}  line:{1} lineNr:{2} grblBufferFree:{3}", streamingBuffer.IndexSent, streamingBuffer.GetSentLine(), streamingBuffer.GetSentLineNr(), grblBufferFree);
                            addToLog("[Pause streaming - skip M0 line:" + streamingBuffer.GetSentLineNr() + "]");
                            lineStreamingPause = streamingBuffer.GetSentLineNr();

                            if (grblStateNow == grblState.idle)
                            {
                                requestSend("G4 P1");   // changed from 2 to 1 2021-01-26
                                grblStateNow = grblStateLast = grblState.unknown;
                                countPreventInterlock = 10;
                            }

                            isStreamingRequestPause = true;
                            streamingStateNow = grblStreaming.waitidle;
                            waitForIdle = true;                         // block further sending
                            waitForOk = false;
                            getParserState = true;                      // ask for parser state
                            line = "(M0)";
                        }
                    }
                    #endregion

                    if (isMarlin)
                    {   if (updateMarlinPosition || (--insertMarlinCounter <= 0))
                        {   //requestSend("M114", streamingBuffer.GetSentLineNr(), false);    // insert getPosition commands
                            sendBuffer.Add("M114", streamingBuffer.GetSentLineNr());
                            getMarlinPositionWasSent = true;
                            streamingBuffer.LineWasSent();
                            streamingStateOld = streamingStateNow;
                            lengthToSend = streamingBuffer.LengthSent() + 1;    // update while-variable
                            insertMarlinCounter = insertMarlinCounterReload;
                        }
                        updateMarlinPosition = false;
                    }
         //           requestSend(line, streamingBuffer.GetSentLineNr(), false);   // fill sendBuffer, 
                    sendBuffer.Add(line, streamingBuffer.GetSentLineNr());
                    if (logEnable) System.IO.File.AppendAllText(Application.StartupPath + "\\logSendBuffer.nc", line + "\r\n"); // clear file
                    streamingBuffer.LineWasSent();
                    streamingStateOld = streamingStateNow;
                    lengthToSend = streamingBuffer.LengthSent() + 1;    // update while-variable
                }   // lock
                processSend();
                //                Logger.Trace("preProcessStreaming sent {0}  lengthToSend {1}  grblBufferFree {2} 3busy {3} countPreventIdle {4} line {5}", streamingBuffer.IndexSent, lengthToSend, grblBufferFree, serial3Busy, countPreventIdle, line);
            }   // while

            if (streamingStateNow != grblStreaming.pause)
            {   if (trgEvent) sendStreamEvent(streamingStateNow); trgEvent = false; }    // streaming in preProcessStreaming
        }


/***************************************************************
* streamingIDLE()
* called in RX event chain, from processGrblStateChange()
***************************************************************/
        private void streamingIDLE()
        {
            // in main GUI: send extra Pause-Code in MainTimer_Tick from Properties.Settings.Default.flowControlText
            // OnRaiseStreamEvent - case grblStreaming.pause: if (isStreamingPauseFirst && Properties.Settings.Default.flowControlEnable) delayedSend = 2;
            if (countPreventIdle <= 1)
            {
                waitForIdle = false;
//                addToLog("---------- IDLE state reached ---------");
                if (logStartStop) Logger.Trace(" grblStateChanged() ---------- IDLE state reached --------- {0}", listInfoStream());
                if ((isStreamingRequestPause || isStreamingRequestStopp) && !isStreamingPause)
                {
                    isStreamingPause = true;
                    isStreamingRequestPause = false;
                    streamingStateNow = grblStreaming.pause;
                    posPause = posWork;
                    addToLog(string.Format("[Save Settings X:{0} Y:{1}]", posPause.X, posPause.Y));

                    if (logStartStop) Logger.Trace("updateStreaming IDLE reached");
                    gcodeVariable["MLAX"] = posMachine.X; gcodeVariable["MLAY"] = posMachine.Y; gcodeVariable["MLAZ"] = posMachine.Z;
                    gcodeVariable["WLAX"] = posWork.X; gcodeVariable["WLAY"] = posWork.Y; gcodeVariable["WLAZ"] = posWork.Z;

                    if (getParserState)
                    {   requestSend("$G"); }

                    if (isStreamingRequestStopp)	// 20200717
					{	
						if (logStartStop) Logger.Trace(" grblStateChanged() - now really stop - resetStreaming");
						stopStreamingFinal();
                        isStreaming = false;
					}
                    updateControls();
                }

                if (streamingStateOld != streamingStateNow)
                    sendStreamEvent(streamingStateNow);     // streaming in streamingIDLE()
                streamingStateOld = streamingStateNow;

                if (streamingBuffer.IndexConfirmed >= streamingBuffer.Count)
                {   streamingFinish(); }
            }
        }

/***** last command was sent/received - end streaming *****/
        private void streamingFinish()
        {
            countPreventEvent = 0; countPreventOutput = 0;

            addToLog("\r[Streaming finish]");
            Logger.Info("streamingFinish ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲");
            sendStreamEvent(streamingStateNow);     // streaming in streamingFinish()
            streamingStateNow = grblStreaming.finish;

            OnRaiseStreamEvent(new StreamEventArgs(streamingBuffer.MaxLineNr, streamingBuffer.MaxLineNr, 100, 0, grblStreaming.finish));
            if (Properties.Settings.Default.grblPollIntervalReduce)
            {
                timerSerial.Interval = grbl.pollInterval;
                countMissingStatusReport = (int)(10000 / timerSerial.Interval);
            }
            sendBuffer.Clear();
            streamingBuffer.Clear();
            resetStreaming();
            updateControls();
            if (isStreamingCheck)
            { requestSend("$C"); isStreamingCheck = false; }
        }

        private void setToolChangeCoordinates(int cmdTNr, string line="")
        { 
            toolProp toolInfo = toolTable.getToolProperties(cmdTNr);
            gcodeVariable["TOAN"] = cmdTNr;
            if (toolInfo.toolnr != cmdTNr)
            {   gcodeVariable["TOAX"] = gcodeVariable["TOAY"] = gcodeVariable["TOAZ"] = gcodeVariable["TOAA"] = 0;
                if (cBStatus1.Checked || cBStatus.Checked) addToLog("\r[Tool change: " + cmdTNr.ToString() + " no Information found! (" + line + ")]");
            }
            else
            {   // get new values
                double tx, ty, tz, ta;
                gcodeVariable["TOAX"] = tx = (double)toolInfo.X + (double)Properties.Settings.Default.toolTableOffsetX;
                gcodeVariable["TOAY"] = ty = (double)toolInfo.Y + (double)Properties.Settings.Default.toolTableOffsetY;
                gcodeVariable["TOAZ"] = tz = (double)toolInfo.Z + (double)Properties.Settings.Default.toolTableOffsetZ;
                gcodeVariable["TOAA"] = ta = (double)toolInfo.A + (double)Properties.Settings.Default.toolTableOffsetA;
                string coord = string.Format("X:{0:0.0} Y:{1:0.0} Z:{2:0.0} A:{3:0.0}",tx,ty,tz,ta);
                if (cBStatus1.Checked || cBStatus.Checked)  addToLog("\r[set tool coordinates " + cmdTNr.ToString() + " " + coord +  "]");
            }
        }

        private int insertComment(int index, int linenr, string cmt)
        {
            if (!streamingBuffer.Insert(index, cmt, linenr))
                Logger.Error("insertComment nok index:{0} buffer:{1}",index, streamingBuffer.Count);
            index++;
            return index;
        }
    /*    private int insertCode(string file, int index, int linenr, bool replace=false)
        {
            if (File.Exists(file))
            {
                string fileCmd = File.ReadAllText(file);
                string[] commands = fileCmd.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                string tmp;
                foreach (string cmd in commands)
                {
                    tmp = cleanUpCodeLine(cmd);         // remove comments
                    if (replace)
                        tmp = insertVariable(tmp);
                    if (tmp.Length > 0)
                    {
                        streamingBuffer.Insert(index, tmp,linenr);
                        index++;
                    }
                }
            }
            return index;
        }*/
    }
}
