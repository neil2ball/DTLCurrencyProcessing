﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using log4net;
using Nwc.XmlRpc;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using System.Reflection;
using System.Collections;
using OpenMetaverse;
using OpenSim.Data.MySQL.MySQLMoneyDataWrapper;
using Nini.Config;

namespace OpenSim.Grid.MoneyServer
{
    class MoneyXmlRpcModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private int m_defaultBalance = 0;

        private string m_simServURI = string.Empty;

        private string m_confirmURI = string.Empty;

        const int MONEYMODULE_REQUEST_TIMEOUT = 30 * 1000;//30 seconds

        private long TicksToEpoch = new DateTime(1970, 1, 1).Ticks;

        private IMoneyDBService m_moneyDBService;

        private IMoneyServiceCore m_moneyCore;

        protected IConfig m_config;
        /// <value>
        /// Used to notify old regions as to which OpenSim version to upgrade to
        /// </value>
        private string m_opensimVersion;

        private Dictionary<string, string> m_sessionDic;

        private Dictionary<string, string> m_secureSessionDic;

        private Dictionary<string, string> m_webSessionDic;

        protected BaseHttpServer m_httpServer;

        public MoneyXmlRpcModule()
        {
        }
        public void Initialise(string opensimVersion,IConfig config, IMoneyDBService moneyDBService, IMoneyServiceCore moneyCore) 
  
        {
            m_opensimVersion = opensimVersion;
            m_moneyDBService = moneyDBService;
            m_moneyCore = moneyCore;
            m_config = config;

            m_defaultBalance = m_config.GetInt("DefaultBalance", 0);
            m_confirmURI = m_config.GetString("ConfirmURL", "https://SampleServer.com/confirm.aspx");

            //m_config = config;
            m_sessionDic = m_moneyCore.GetSessionDic();
            m_secureSessionDic = m_moneyCore.GetSecureSessionDic();
            m_webSessionDic = m_moneyCore.GetWebSessionDic();
            RegisterHandlers();
        }
        public void PostInitialise()
        {    
        }
        public void RegisterHandlers()
        {
            //have these in separate method as some servers restart the http server and reregister all the handlers.
            m_httpServer = m_moneyCore.GetHttpServer();
            m_httpServer.AddXmlRPCHandler("ClientLogin", handleClientLogin);
            m_httpServer.AddXmlRPCHandler("TransferMoney", handleTransaction);
            m_httpServer.AddXmlRPCHandler("GetBalance", handleSimulatorUserBalanceRequest);
            m_httpServer.AddXmlRPCHandler("ClientLogout", handleClientLogout);
            m_httpServer.AddXmlRPCHandler("ConfirmTransfer", handleConfirmTransfer);
            m_httpServer.AddXmlRPCHandler("CancelTransfer", handleCancelTransfer);
            m_httpServer.AddXmlRPCHandler("GetTransaction", handleGetTransaction);
            m_httpServer.AddXmlRPCHandler("WebLogin", handleWebLogin);
            m_httpServer.AddXmlRPCHandler("WebLogout", handleWebLogout);
            m_httpServer.AddXmlRPCHandler("WebGetBalance", handleWebGetBalance);
            m_httpServer.AddXmlRPCHandler("WebGetTransaction", handleWebGetTransaction);
            m_httpServer.AddXmlRPCHandler("WebGetTransactionNum", handleWebGetTransactionNum);
        }

        /// <summary>
        /// Get the user balance when user entering a parcel.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse handleClientLogin(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            string clientUUID = string.Empty;
            string clientSessionID = string.Empty;
            string clientSecureSessionID = string.Empty;
            string userServerIP = string.Empty;
            string userID = string.Empty;
            string simIP = string.Empty;
            string avatarName = string.Empty;
            int balance = 0;

            response.Value = responseData;

            if (requestData.ContainsKey("clientUUID"))
                clientUUID = (string)requestData["clientUUID"];

            if (requestData.ContainsKey("clientSessionID"))
                clientSessionID = (string)requestData["clientSessionID"];

            if (requestData.ContainsKey("clientSecureSessionID"))
                clientSecureSessionID = (string)requestData["clientSecureSessionID"];

            if (requestData.ContainsKey("userServIP"))
                userServerIP = (string)requestData["userServIP"];

            if (requestData.ContainsKey("openSimServIP"))
                simIP = (string)requestData["openSimServIP"];

            if (requestData.ContainsKey("userName"))
                avatarName = (string)requestData["userName"];

            userID = clientUUID + "@" + userServerIP;

            //Update the session and secure session dictionary
            lock (m_sessionDic)
            {
                if (!m_sessionDic.ContainsKey(userID))
                {
                    m_sessionDic.Add(userID, clientSessionID);
                }
                else m_sessionDic[userID] = clientSessionID;
            }
            lock (m_secureSessionDic)
            {
                if (!m_secureSessionDic.ContainsKey(userID))
                {
                    m_secureSessionDic.Add(userID, clientSecureSessionID);
                }
                else m_secureSessionDic[userID] = clientSecureSessionID;
            }

            try
            {
                m_log.InfoFormat("[Money] User: {0} has logged in,getting balance...", userID);
                balance = m_moneyDBService.getBalance(userID);
                //add user if not exist
                if (balance == -1)
                {
                    if (m_moneyDBService.addUser(userID, m_defaultBalance, 0))
                    {
                        responseData["success"] = true;
                        responseData["description"] = "add user successfully";
                        responseData["clientBalance"] = m_defaultBalance;
                    }
                    else
                    {
                        responseData["success"] = false;
                        responseData["description"] = "add user failed";
                        responseData["clientBalance"] = 0;
                    }
                }
                else if (balance >= 0) //Success
                {
                    responseData["success"] = true;
                    responseData["description"] = "get user balance successfully";
                    responseData["clientBalance"] = balance;
                }
                UserInfo user = new UserInfo();
                user.UserID = userID;
                user.SimIP = simIP;
                user.Avatar = avatarName;
                //TODO: Add password protection here
                user.PswHash = UUID.Zero.ToString();

                if (!m_moneyDBService.TryAddUserInfo(user))
                {
                    m_log.ErrorFormat("[MYSQL]Unable to refresh information for user \"{0}\" in DB", avatarName);
                    responseData["success"] = false;
                    responseData["description"] = "Update or add user information to db failed";
                    responseData["clientBalance"] = balance;
                }
                return response;

            }
            catch (Exception e)
            {
                m_log.ErrorFormat("Can't get balance of user {0}", clientUUID);
                responseData["success"] = false;
                responseData["description"] = "Exception occured" + e.ToString();
                responseData["clientBalance"] = 0;
            }
            return response;

        }


        /// <summary>
        /// handle incoming transaction
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse handleTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            response.Value = responseData;

            string senderID = string.Empty;
            string receiverID = string.Empty;
            string senderSessionID = string.Empty;
            string senderSecureSessionID = string.Empty;
            int amount = 0;
            string localID = string.Empty;
            string regionHandle = string.Empty;
            int transactionType = 0;
            string description = string.Empty;
            string senderUserServIP = string.Empty;
            string receiverUserServIP = string.Empty;

            string fromID = string.Empty;
            string toID = string.Empty;
            UUID transactionUUID = UUID.Random();

            if (requestData.ContainsKey("senderID"))
                senderID = (string)requestData["senderID"];

            if (requestData.ContainsKey("receiverID"))
                receiverID = (string)requestData["receiverID"];

            if (requestData.ContainsKey("senderSessionID"))
                senderSessionID = (string)requestData["senderSessionID"];

            if (requestData.ContainsKey("senderSecureSessionID"))
                senderSecureSessionID = (string)requestData["senderSecureSessionID"];

            if (requestData.ContainsKey("amount"))
                amount = (Int32)requestData["amount"];

            if (requestData.ContainsKey("localID"))
                localID = (string)requestData["localID"];

            if (requestData.ContainsKey("regionHandle"))
                regionHandle = (string)requestData["regionHandle"];

            if (requestData.ContainsKey("transactionType"))
                transactionType = (Int32)requestData["transactionType"];

            if (requestData.ContainsKey("description"))
                description = (string)requestData["description"];

            if (requestData.ContainsKey("senderUserServIP"))
                senderUserServIP = (string)requestData["senderUserServIP"];

            if (requestData.ContainsKey("receiverUserServIP"))
                receiverUserServIP = (string)requestData["receiverUserServIP"];

            fromID = senderID + "@" + senderUserServIP;
            toID = receiverID + "@" + receiverUserServIP;

            if (m_sessionDic.ContainsKey(fromID) && m_secureSessionDic.ContainsKey(fromID))
            {
                if (m_sessionDic[fromID] == senderSessionID && m_secureSessionDic[fromID] == senderSecureSessionID)
                {
                    m_log.InfoFormat("[Money] Transfering money from {0} to {1}", fromID, toID);
                    int time = (int)((DateTime.Now.Ticks - TicksToEpoch) / 10000000);
                    try
                    {
                        TransactionData transaction = new TransactionData();
                        transaction.TransUUID = transactionUUID;
                        transaction.Sender = fromID;
                        transaction.Receiver = toID;
                        transaction.Amount = amount;
                        transaction.ObjectUUID = localID;
                        transaction.RegionHandle = regionHandle;
                        transaction.Type = transactionType;
                        transaction.Time = time;
                        transaction.SecureCode = UUID.Random().ToString();
                        transaction.Status = (int)Status.PENDING_STATUS;
                        transaction.Description = "Newly added on " + DateTime.Now.ToString();

                        bool result = m_moneyDBService.addTransaction(transaction);
                        if (result)
                        {
                            //send a confirm URL to user via HTTPS.
                            m_log.InfoFormat("[Money] Sending confirm link to client:{0},please wait",
                                                      senderID);
                            Hashtable confirmTable = new Hashtable();
                            confirmTable["clientUUID"] = senderID;
                            confirmTable["clientSessionID"] = senderSessionID;
                            confirmTable["clientSecureSessionID"] = senderSecureSessionID;
                            confirmTable["URI"] = m_confirmURI + "?transactionID=" + transactionUUID.ToString() + "&secureCode=" + transaction.SecureCode ;

                            UserInfo user = m_moneyDBService.FetchUserInfo(fromID);
                            if (user != null)
                            {
                                Hashtable resultTable = genericCurrencyXMLRPCRequest(confirmTable, "SendConfirmLink", user.SimIP);


                                if (resultTable != null && resultTable.ContainsKey("success"))
                                {
                                    if ((bool)resultTable["success"])
                                    {
                                        m_log.InfoFormat("[Money] Sent confirm link to client:{0} successfully",
                                                          senderID);

                                        responseData["success"] = true;
                                        return response;
                                    }
                                }
                            }
                            
                        }
                        else // add transaction failed
                        {
                            m_log.ErrorFormat("[Money DB] Add transaction for user:{0} failed",fromID);
                        }

                        responseData["success"] = false;
                        return response;

                    }
                    catch (Exception e)
                    {
                        m_log.Error("[Money DB] Exception occurred while adding transaction " + e.ToString());
                        responseData["success"] = false;
                        return response;
                    }
                    
                }

            }
            m_log.Error("[Money] Session authentication failure for sender: " + fromID);
            responseData["success"] = false;
            responseData["message"] = "Session check failure,please re-login later!";
            return response;
        }


        /// <summary>
        /// Get the user balance.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse handleSimulatorUserBalanceRequest(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            response.Value = responseData;
            string clientUUID = string.Empty;
            string clientSessionID = string.Empty;
            string clientSecureSessionID = string.Empty;
            string userServerIP = string.Empty;
            string userID = string.Empty;
            int balance;

            if (requestData.ContainsKey("clientUUID"))
                clientUUID = (string)requestData["clientUUID"];
            if (requestData.ContainsKey("clientSessionID"))
                clientSessionID = (string)requestData["clientSessionID"];
            if (requestData.ContainsKey("clientSecureSessionID"))
                clientSecureSessionID = (string)requestData["clientSecureSessionID"];
            if (requestData.ContainsKey("userServIP"))
                userServerIP = (string)requestData["userServIP"];

            userID = clientUUID + "@" + userServerIP;

            m_log.InfoFormat("[Money] Getting balance for user: {0}", userID);
            if (m_sessionDic.ContainsKey(userID) && m_secureSessionDic.ContainsKey(userID))
            {
                if (m_sessionDic[userID] == clientSessionID && m_secureSessionDic[userID] == clientSecureSessionID)
                {
                    try
                    {
                        balance = m_moneyDBService.getBalance(userID);

                        if (balance == -1) // User not found
                        {
                            if (m_moneyDBService.addUser(userID, m_defaultBalance, 0))
                            {
                                responseData["success"] = true;
                                responseData["description"] = "add user successfully";
                                responseData["clientBalance"] = m_defaultBalance;
                            }
                            else
                            {
                                responseData["success"] = false;
                                responseData["description"] = "add user failed";
                                responseData["clientBalance"] = 0;
                            }
                        }

                        else if (balance >= 0)
                        {
                            responseData["success"] = true;
                            responseData["clientBalance"] = balance;
                        }
                        return response;
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[Money DB] Can't get balance for user {0},Exception: {1}", clientUUID, e.ToString());
                    }

                }
            }

            m_log.Error("[Money] Session authentication failed when getting balance for user: " + userID);

            responseData["success"] = false;
            responseData["description"] = "Session check failure,please re-login";
            return response;
        }



        public XmlRpcResponse handleClientLogout(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            string clientUUID = string.Empty;
            string clientSessionID = string.Empty;
            string clientSecureSessionID = string.Empty;
            string userServerIP = string.Empty;
            string userID = string.Empty;

            response.Value = responseData;

            if (requestData.ContainsKey("clientUUID"))
                clientUUID = (string)requestData["clientUUID"];

            if (requestData.ContainsKey("clientSessionID"))
                clientSessionID = (string)requestData["clientSessionID"];

            if (requestData.ContainsKey("clientSecureSessionID"))
                clientSecureSessionID = (string)requestData["clientSecureSessionID"];

            if (requestData.ContainsKey("userServIP"))
                userServerIP = (string)requestData["userServIP"];

            userID = clientUUID + "@" + userServerIP;

            m_log.InfoFormat("[Money] User {0} is logging off", userID);
            try
            {

                lock (m_sessionDic)
                {
                    if (m_sessionDic.ContainsKey(userID))
                    {
                        m_sessionDic.Remove(userID);
                    }
                }

                lock (m_secureSessionDic)
                {
                    if (m_secureSessionDic.ContainsKey(userID))
                    {
                        m_secureSessionDic.Remove(userID);
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error("[Money]: Failed to delete user session " + e.ToString() );
                responseData["success"] = false;
            }
            responseData["success"] = true;

            return response;

        }


        /// <summary>
        /// Continue with the transaction if user clicks the confirm link.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse handleConfirmTransfer(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            string secureCode = string.Empty;
            string transactionID = string.Empty;
            UUID transactionUUID = UUID.Zero;

            response.Value = responseData;

            if (requestData.ContainsKey("secureCode"))
                secureCode = (string)requestData["secureCode"];
            if (requestData.ContainsKey("transactionID"))
            {
                transactionID = (string)requestData["transactionID"];
                UUID.TryParse(transactionID,out transactionUUID);
            }
            if (string.IsNullOrEmpty(secureCode) || string.IsNullOrEmpty(transactionID))
            {
                responseData["success"] = false;
                m_log.Error("[Money] secureCode or transactionID can't be empty");
                return response;
            }

            m_log.InfoFormat("[Money] User has accepted the transaction,now continue with the transaction");

            try
            {
                if (m_moneyDBService.ValidateTransfer(secureCode, transactionUUID))
                {
                    if (m_moneyDBService.DoTransfer(transactionUUID))
                    {
                        //update balance
                        TransactionData transaction = m_moneyDBService.FetchTransaction(transactionUUID);
                        if (transaction != null && transaction.Status == (int)Status.SUCCESS_STATUS)
                        {
                            m_log.InfoFormat("[Money] Payment finished successfully,now update balance", transactionID);
                            UpdateBalance(transaction.Sender);
                            UpdateBalance(transaction.Receiver);
                            //Notify opensim that transaction has been finished successfully and give item to the customer.

                            if (transaction.Type == 5008)
                            {
                                m_log.InfoFormat("[Money] Now notify opensim to give object to customer:{0} ", transaction.Sender);
                                Hashtable requestTable = new Hashtable();
                                string senderID = transaction.Sender.Split('@')[0];
                                string receiverID = transaction.Receiver.Split('@')[0];
                                requestTable["senderID"] = senderID;
                                requestTable["receiverID"] = receiverID;
                                if(m_sessionDic.ContainsKey(transaction.Sender)&&m_secureSessionDic.ContainsKey(transaction.Sender))
                                {
                                    requestTable["senderSessionID"] = m_sessionDic[transaction.Sender];
                                    requestTable["senderSecureSessionID"] = m_secureSessionDic[transaction.Sender];
                                }
                                else 
                                {
                                    requestTable["senderSessionID"] =  UUID.Zero.ToString();
                                    requestTable["senderSecureSessionID"] = UUID.Zero.ToString();
                                }
                                requestTable["transactionType"] = transaction.Type;
                                requestTable["amount"] = transaction.Amount;
                                requestTable["localID"] = transaction.ObjectUUID;
                                requestTable["regionHandle"] = transaction.RegionHandle;
                                UserInfo user = m_moneyDBService.FetchUserInfo(transaction.Sender);
                                if (user != null)
                                {
                                    Hashtable responseTable = genericCurrencyXMLRPCRequest(requestTable, "OnMoneyTransfered", user.SimIP);

                                    if (responseTable != null && responseTable.ContainsKey("success"))
                                    {
                                        //User not online or failed to get object ?
                                        if (!(bool)responseTable["success"])
                                        {
                                            m_log.ErrorFormat("[Money] User: {0} can't get the object,rolling back", transaction.Sender);
                                            if (RollBackTransaction(transaction))
                                            {
                                                m_log.ErrorFormat("[Money] transaction: {0} failed but roll back succeeded", transactionID);
                                            }
                                            else
                                            {
                                                m_log.ErrorFormat("[Money] Fatal error,transaction: {0} failed and roll back failed as well", transactionID);
                                            }

                                        }
                                        else
                                        {
                                            m_log.InfoFormat("Object has been given,transaction: {0} finished successfully.",transactionID);
                                            responseData["success"] = true;
                                            return response;
                                        }
                                    }
                                }
                                responseData["success"] = false;
                                return response;
                            }
                            responseData["success"] = true;
                            return response;
                        }
                        
                    }
                }
                m_log.ErrorFormat("[Money] Transaction:{0} failed", transactionID);
                responseData["success"] = false;
                responseData["message"] = "Transfer Money failed";
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[Money] Exception occurred when transfering money in the transaction {0}: {1} ", transactionID,e.ToString());
                responseData["success"] = false;
            }
            return response;
        }


        //This bit of code is taken from OpenSim.

        /// <summary>   
        /// Generic XMLRPC client abstraction
        /// </summary>   
        /// <param name="ReqParams">Hashtable containing parameters to the method</param>   
        /// <param name="method">Method to invoke</param>   
        /// <returns>Hashtable with success=>bool and other values</returns>   
        private Hashtable genericCurrencyXMLRPCRequest(Hashtable reqParams, string method,string uri)
        {
            // Handle the error in parameter list.   
            if (reqParams.Count <= 0 ||
                string.IsNullOrEmpty(method) ||
                !uri.StartsWith("http://"))
            {
                return null;
            }

            ArrayList arrayParams = new ArrayList();
            arrayParams.Add(reqParams);
            XmlRpcResponse moneyServResp = null;
            try
            {
                XmlRpcRequest moneyModuleReq = new XmlRpcRequest(method, arrayParams);
                moneyServResp = moneyModuleReq.Send(uri,
                                                    MONEYMODULE_REQUEST_TIMEOUT);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat(
                    "[MONEY]: Unable to connect to OpenSim Server {0}.  Exception {1}",
                    uri, ex.ToString());

                Hashtable ErrorHash = new Hashtable();
                ErrorHash["success"] = false;
                ErrorHash["errorMessage"] = "Failed to perform actions on OpenSim Server";
                ErrorHash["errorURI"] = "";

                return ErrorHash;
            }

            if (moneyServResp.IsFault)
            {
                Hashtable ErrorHash = new Hashtable();
                ErrorHash["success"] = false;
                ErrorHash["errorMessage"] = "Failed to perform actions on OpenSim Server";
                ErrorHash["errorURI"] = "";

                return ErrorHash;
            }
            Hashtable moneyRespData = (Hashtable)moneyServResp.Value;

            return moneyRespData;
        }

        /// <summary>
        /// Update the client balance.We don't care about the result.
        /// </summary>
        /// <param name="userID"></param>
        private void UpdateBalance(string userID)
        {
            string clientUUID = string.Empty;
            string clientSessionID = string.Empty;
            string clientSecureSessionID = string.Empty;
            if (m_sessionDic.ContainsKey(userID) && m_secureSessionDic.ContainsKey(userID))
            {
                clientSessionID = m_sessionDic[userID];
                clientSecureSessionID = m_secureSessionDic[userID];
                clientUUID = userID.Split('@')[0];
                Hashtable requestTable = new Hashtable();
                requestTable["clientUUID"] = clientUUID;
                requestTable["clientSessionID"] = clientSessionID;
                requestTable["clientSecureSessionID"] = clientSecureSessionID;
                requestTable["Balance"] = m_moneyDBService.getBalance(userID);
                UserInfo user = m_moneyDBService.FetchUserInfo(userID);
                if (user != null)
                {
                    genericCurrencyXMLRPCRequest(requestTable, "UpdateBalance",user.SimIP);
                }
            }
        }



        /// <summary>
        /// RollBack the transaction if user failed to get the object paid
        /// </summary>
        /// <param name="transaction"></param>
        /// <returns></returns>
        protected bool RollBackTransaction(TransactionData transaction)
        {
            if(m_moneyDBService.withdrawMoney(transaction.TransUUID,transaction.Receiver,transaction.Amount))
            {
                if(m_moneyDBService.giveMoney(transaction.TransUUID,transaction.Sender,transaction.Amount))
                {
                    m_log.InfoFormat("Roll back transaction{0} successfully", transaction.TransUUID.ToString());
                    m_moneyDBService.updateTransactionStatus(transaction.TransUUID, (int)Status.FAILED_STATUS,
                        "The buyer failed to get the object,roll back the transaction");
                    UpdateBalance(transaction.Sender);
                    UpdateBalance(transaction.Receiver);
                    return true;
                }
            }
            return false;
        }

        public XmlRpcResponse handleCancelTransfer(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            string secureCode = string.Empty;
            string transactionID = string.Empty;
            UUID transactionUUID = UUID.Zero;

            response.Value = responseData;

            if (requestData.ContainsKey("secureCode"))
                secureCode = (string)requestData["secureCode"];
            if (requestData.ContainsKey("transactionID"))
            {
                transactionID = (string)requestData["transactionID"];
                UUID.TryParse(transactionID,out transactionUUID);
            }
            if (string.IsNullOrEmpty(secureCode) || string.IsNullOrEmpty(transactionID))
            {
                responseData["success"] = false;
                m_log.Error("secureCode or transactionID can't be empty");
                return response;
            }

            TransactionData tran = m_moneyDBService.FetchTransaction(transactionUUID);
            UserInfo user = m_moneyDBService.FetchUserInfo(tran.Sender);
         
            try
            {
                m_log.InfoFormat("[Money] User: {0} wanted to cancel the transaction", user.Avatar);
                if (m_moneyDBService.ValidateTransfer(secureCode, transactionUUID))
                {
                    m_log.InfoFormat("User:{0} has canceled the transaction:{1}", user.Avatar, transactionID);
                    m_moneyDBService.updateTransactionStatus(transactionUUID, (int)Status.FAILED_STATUS,
                        "User canceled the transaction on " + DateTime.Now.ToString());
                    responseData["success"] = true;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[Money] Exception occurred when canceling the transaction {0}: {1} ", transactionID, e.ToString());
                responseData["success"] = false;
            }
            return response;
        }

        public XmlRpcResponse handleGetTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            TransactionData transaction = new TransactionData();
            response.Value = responseData;
            string secureCode = string.Empty;
            string transactionID = string.Empty;
            UUID transactionUUID = UUID.Zero;
            if (requestData.ContainsKey("transactionID"))
            {
                transactionID = (string)requestData["transactionID"];
                UUID.TryParse(transactionID, out transactionUUID);
            }
            if(string.IsNullOrEmpty(transactionID))
            {
                            
                responseData["success"] = false;
                responseData["description"] = "TransactionID can`t be empty";
                m_log.Error("[Money] TransactionID can't be empty");
                return response;
            }
            transaction = m_moneyDBService.FetchTransaction(transactionUUID);
            if (transaction!=null)
            {
                UserInfo senderInfo = m_moneyDBService.FetchUserInfo(transaction.Sender);
                UserInfo receiverInfo = m_moneyDBService.FetchUserInfo(transaction.Receiver);
                if (senderInfo != null && receiverInfo != null)
                {
                    responseData["success"] = true;
                    responseData["sender"] = senderInfo.Avatar;
                    responseData["receiver"] = receiverInfo.Avatar;
                }
                else
                {
                    responseData["success"] = false;
                    responseData["sender"] = "Query failed";
                    responseData["receiver"] = "Query failed";
                }
                responseData["amount"] = transaction.Amount;
                responseData["time"] = transaction.Time;
            }
            return response;

        }


        public XmlRpcResponse handleWebLogin(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            string userID = string.Empty;
            string webSessionID = string.Empty;


            response.Value = responseData;

            if (requestData.ContainsKey("userID"))
                userID = (string)requestData["userID"];

            if (requestData.ContainsKey("sessionID"))
                webSessionID = (string)requestData["sessionID"];

            if (string.IsNullOrEmpty(userID) || string.IsNullOrEmpty(webSessionID))
            {
                responseData["success"] = false;
                responseData["errorMessage"] = "userID or sessionID can`t be empty,login failed!";
                return response;
            }

            //Update the web session dictionary
            lock (m_webSessionDic)
            {
                if (!m_webSessionDic.ContainsKey(userID))
                {
                    m_webSessionDic.Add(userID, webSessionID);
                }
                else m_webSessionDic[userID] = webSessionID;
            }

            m_log.InfoFormat("[WEB] User: {0} has logged in from web", userID);
            responseData["success"] = true;
            return response;

        }

        public XmlRpcResponse handleWebLogout(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            string userID = string.Empty;
            string webSessionID = string.Empty;


            response.Value = responseData;

            if (requestData.ContainsKey("userID"))
                userID = (string)requestData["userID"];

            if (requestData.ContainsKey("sessionID"))
                webSessionID = (string)requestData["sessionID"];

            if (string.IsNullOrEmpty(userID) || string.IsNullOrEmpty(webSessionID))
            {
                responseData["success"] = false;
                responseData["errorMessage"] = "userID or sessionID can`t be empty,log out failed!";
                return response;
            }

            //Update the web session dictionary
            lock (m_webSessionDic)
            {
                if (m_webSessionDic.ContainsKey(userID))
                {
                    m_webSessionDic.Remove(userID);
                }
            }

            m_log.InfoFormat("[WEB] User: {0} has logged out from web", userID);
            responseData["success"] = true;
            return response;

        }


        /// <summary>
        /// Get balance method for web pages.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse handleWebGetBalance(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            string userID = string.Empty;
            string webSessionID = string.Empty;
            int balance = 0;

            response.Value = responseData;

            if (requestData.ContainsKey("userID"))
                userID = (string)requestData["userID"];

            if (requestData.ContainsKey("sessionID"))
                webSessionID = (string)requestData["sessionID"];
            

            m_log.InfoFormat("[Money] Getting balance for user: {0}", userID);

            if (m_webSessionDic.ContainsKey(userID)) //perform session check
            {
                if (m_webSessionDic[userID] == webSessionID)
                {
                    try
                    {
                        balance = m_moneyDBService.getBalance(userID);
                        UserInfo user = m_moneyDBService.FetchUserInfo(userID);
                        if (user != null)
                        {
                            responseData["userName"] = user.Avatar;
                        }
                        else
                        {
                            responseData["userName"] = "Unknown user";
                        }
                        if (balance == -1) // User not found
                        {

                                responseData["success"] = false;
                                responseData["errorMessage"] = "User not found";
                                responseData["balance"] = 0;
                        }

                        else if (balance >= 0)
                        {
                            responseData["success"] = true;
                            responseData["balance"] = balance;
                        }
                        return response;
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[Money DB] Can't get balance for user {0},Exception: {1}", userID, e.ToString());
                        responseData["success"] = false;
                        responseData["errorMessage"] = "Exception occurred when getting balance";
                        return response;
                    }

                }
            }

            m_log.Error("[Money] Session authentication failed when getting balance for user: " + userID);

            responseData["success"] = false;
            responseData["errorMessage"] = "Session check failure,please re-login";
            return response;
        }


        /// <summary>
        /// Get transaction for web pages
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse handleWebGetTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            string userID = string.Empty;
            string webSessionID = string.Empty;
            int lastIndex = -1;
            int startTime = 0;
            int endTime = 0;

            response.Value = responseData;

            if (requestData.ContainsKey("userID"))
                userID = (string)requestData["userID"];

            if (requestData.ContainsKey("sessionID"))
                webSessionID = (string)requestData["sessionID"];

            if (requestData.ContainsKey("startTime"))
                startTime = (int)requestData["startTime"];

            if (requestData.ContainsKey("endTime"))
                endTime = (int)requestData["endTime"];

            if (requestData.ContainsKey("lastIndex"))
                lastIndex = (int)requestData["lastIndex"];

            if (m_webSessionDic.ContainsKey(userID))
            {
                if (m_webSessionDic[userID] == webSessionID)
                {
                    try
                    {
                        int total = m_moneyDBService.getTransactionNum(userID, startTime, endTime);
                        TransactionData tran = null;
                        m_log.InfoFormat("[WEB] Getting transation[{0}] for user: {1}", lastIndex + 1, userID);
                        if (total > lastIndex + 2)
                        {
                            responseData["isEnd"] = false;
                        }
                        else
                        {
                            responseData["isEnd"] = true;
                        }
                        tran = m_moneyDBService.FetchTransaction(userID, startTime, endTime, lastIndex);
                        if (tran != null)
                        {
                            UserInfo senderInfo = m_moneyDBService.FetchUserInfo(tran.Sender);
                            UserInfo receiverInfo = m_moneyDBService.FetchUserInfo(tran.Receiver);
                            if (senderInfo != null && receiverInfo != null)
                            {
                                responseData["senderName"] = senderInfo.Avatar;
                                responseData["receiverName"] = receiverInfo.Avatar;
                            }
                            else
                            {
                                responseData["senderName"] = "Unknown user";
                                responseData["receiverName"] = "Unknown user";
                            }
                            responseData["success"] = true;
                            responseData["transactionIndex"] = lastIndex + 1;
                            responseData["transactionUUID"] = tran.TransUUID.ToString();
                            responseData["senderID"] = tran.Sender;
                            responseData["receiverID"] = tran.Receiver;
                            responseData["amount"] = tran.Amount;
                            responseData["type"] = tran.Type;
                            responseData["time"] = tran.Time;
                            responseData["status"] = tran.Status;
                            responseData["description"] = tran.Description;
                        }
                        else
                        {
                            responseData["success"] = false;
                            responseData["errorMessage"] = string.Format("Unable to fetch transaction data with the index {0}",
                                lastIndex + 1);
                        }
                        return response;
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[Money DB] Can't get transaction for user {0},Exception: {1}", userID, e.ToString());
                        responseData["success"] = false;
                        responseData["errorMessage"] = "Exception occurred when getting transaction";
                        return response;
                    }
                }
            }

            m_log.Error("[Money] Session authentication failed when getting transaction for user: " + userID);

            responseData["success"] = false;
            responseData["errorMessage"] = "Session check failure,please re-login";
            return response;

        }


        /// <summary>
        /// Get total number of transactions for web pages.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse handleWebGetTransactionNum(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            string userID = string.Empty;
            string webSessionID = string.Empty;
            int startTime = 0;
            int endTime = 0;

            response.Value = responseData;

            if (requestData.ContainsKey("userID"))
                userID = (string)requestData["userID"];

            if (requestData.ContainsKey("sessionID"))
                webSessionID = (string)requestData["sessionID"];

            if (requestData.ContainsKey("startTime"))
                startTime = (int)requestData["startTime"];

            if (requestData.ContainsKey("endTime"))
                endTime = (int)requestData["endTime"];

            if (m_webSessionDic.ContainsKey(userID))
            {
                if (m_webSessionDic[userID] == webSessionID)
                {
                    int it = m_moneyDBService.getTransactionNum(userID,startTime,endTime);
                    if (it >= 0)
                    {
                        m_log.InfoFormat("[WEB] Get {0} transactions for user: {1}", it,userID);
                        responseData["success"] = true;
                        responseData["number"] = it;
                    }
                    else
                    {
                        responseData["success"] = false;
                    }
                    return response;
                }
            }

            m_log.Error("[Money] Session authentication failed when getting transaction number for user: " + userID);
            responseData["success"] = false;
            responseData["errorMessage"] = "Session check failure,please re-login";
            return response;
        }

    }
}
