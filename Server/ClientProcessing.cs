﻿using DbLibrary;
using Newtonsoft.Json;
using Shared;
using System;
using System.Collections.Generic;

namespace Server
{
    /// <summary>
    /// Class which process all client messages sended here from ServerConnection,
    /// and gives back server response ready to send to client
    /// </summary>
    public class ClientProcessing
    {
        /// <summary>
        /// Delegate which represents function used to procces client data
        /// </summary>
        public delegate string Functions(string msg, int clientId);

        /// <summary>
        /// List of all avaliable functions, index of given function is number of that option
        /// </summary>
        public List<Functions> functions { get; set; }
        public List<User> activeUsers { get; set; }

        public Dictionary<int,ExtendedInvitation> invitations { get; set; }

        //D(recvUserId:Dict(convId:List<message>))
        public Dictionary<int,Dictionary<int,List<Message>>> messagesToSend { get; set; }

        // D(recvUserId:Dict(convId:notification))
        public Dictionary<int, Dictionary<int, Notification>> notifications { get; set; }
        // userId:convId
        public Dictionary<int,int> activeConversations { get; set; }

        public DbMethods dbMethods { get; set; }

        /// <summary>
        /// Function that takes message from client procces it and return server response
        /// </summary>
        /// <param name="message"> Client message</param>
        /// <returns>Server response ready to send</returns>
        public string ProccesClient(string message, int clientId)
        {
            string[] fields = message.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            int option = int.Parse(fields[0].Split(':', StringSplitOptions.RemoveEmptyEntries)[1]);

            //Remove option
            var list = new List<string>(fields);
            list.RemoveAt(0);

            lock (functions[option])
            {
                return functions[option](string.Join("$$", list), clientId);
            }
        }

        public string Logout(string msg,int clientId)
        {
            lock (activeUsers[clientId])
                activeUsers[clientId].logged = false;
            lock (invitations)
            {
                int[] a = new int[invitations.Count];
                invitations.Keys.CopyTo(a, 0);
                foreach (var key in a)
                {
                    if (invitations[key].reciver == activeUsers[clientId].userName)
                        invitations[key].sended = false;

                }
            }
            return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR,Options.LOGOUT);
        }

        public string Login(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            string username = fields[0].Split(':', StringSplitOptions.RemoveEmptyEntries)[1];
            string password = fields[1].Split(':', StringSplitOptions.RemoveEmptyEntries)[1];

            string passwordHash = "";
            DbMethods dbConnection = new DbMethods();
            lock (activeUsers[clientId]) { dbConnection = activeUsers[clientId].dbConnection; }
            try { passwordHash = dbConnection.GetFromUser("password_hash",username); }
            catch { return TransmisionProtocol.CreateServerMessage(ErrorCodes.USER_NOT_FOUND, Options.LOGIN); }

            if (Security.VerifyPassword(passwordHash, password))
            {
                lock (activeUsers)
                {
                    foreach (User u in activeUsers)
                    {
                        if (u != null)
                        {
                            if (u.userName == username && u.logged)
                            {
                                return TransmisionProtocol.CreateServerMessage(ErrorCodes.USER_ALREADY_LOGGED_IN, Options.LOGIN);
                            }
                        }
                    }
                    activeUsers[clientId].logged = true;
                    activeUsers[clientId].userName = username;
                    activeUsers[clientId].userId = dbConnection.GetUserId(username);

                }
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.LOGIN,dbConnection.GetFromUser("iv_to_decrypt_user_key",username),dbConnection.GetFromUser("user_key_hash", username));
            }
            else return TransmisionProtocol.CreateServerMessage(ErrorCodes.INCORRECT_PASSWORD, Options.LOGIN);
        }

        public string CreateUser(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            string username = fields[0].Split(':', StringSplitOptions.RemoveEmptyEntries)[1];
            string password = fields[1].Split(':', StringSplitOptions.RemoveEmptyEntries)[1];
            string IV = fields[2].Split(':', StringSplitOptions.RemoveEmptyEntries)[1];
            string keyHash = fields[3].Split(':', StringSplitOptions.RemoveEmptyEntries)[1];

            DbMethods dbConnection = new DbMethods();
            lock (activeUsers[clientId]) { dbConnection = activeUsers[clientId].dbConnection; }
            if (dbConnection.CheckIfNameExist(username))
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.USER_ALREADY_EXISTS, Options.CREATE_USER);

            password = Security.HashPassword(password);
            if (dbConnection.AddNewUser(username, password,IV,keyHash)) return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.CREATE_USER);
            else return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.CREATE_USER);
        }

        public string CheckUserName(string msg, int clientId)
        {
            string[] fields = msg.Split("$$");
            string username = fields[0].Split(':')[1];
            DbMethods dbConnection = new DbMethods();
            lock (activeUsers[clientId]) { dbConnection = activeUsers[clientId].dbConnection; }
            if (!dbConnection.CheckIfNameExist(username))
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.CHECK_USER_NAME);
            return TransmisionProtocol.CreateServerMessage(ErrorCodes.USER_ALREADY_EXISTS, Options.CHECK_USER_NAME);
        }

        public string Disconnect(string msg, int clientId)
        {
            lock (activeConversations)
            {
                if (activeConversations.ContainsKey(activeUsers[clientId].userId)) activeConversations.Remove(activeUsers[clientId].userId);
            }
            if (DeleteActiveUser(clientId))
                return "";
            return TransmisionProtocol.CreateServerMessage(ErrorCodes.DISCONNECT_ERROR, Options.DISCONNECT);
        }

        public string GetFriends(string msg, int clientId)
        {
            DbMethods dbConnection = new DbMethods();
            string username;
            List<string> activeUsersNames = new List<string>();

            lock (activeUsers[clientId]) 
            { 
                if (!activeUsers[clientId].logged) return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOT_LOGGED_IN, Options.LOGIN);
                dbConnection = activeUsers[clientId].dbConnection;
                username = activeUsers[clientId].userName;
                foreach(User user in activeUsers)
                {
                    activeUsersNames.Add(user.userName);
                }
            }
            return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR,Options.GET_FRIENDS,dbConnection.GetFriends(username,activeUsersNames));
        }

        // Tested TODO Make errors
        public string SendConversation(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            string secondUserName = fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];
            

            lock(activeUsers[clientId])
            {
                if (!activeUsers[clientId].logged) return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOT_LOGGED_IN, Options.LOGIN);
                string username = activeUsers[clientId].userName;
                int conversationId = activeUsers[clientId].dbConnection.GetConversationId(username, secondUserName);
                string conversation = activeUsers[clientId].redis.GetConversation(conversationId);
                string conversationKey = activeUsers[clientId].dbConnection.GetConversationKey(conversationId, username);
                string conversationIv = activeUsers[clientId].dbConnection.GetConversationIv(conversationId);
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR,Options.GET_CONVERSATION, conversationId.ToString(), conversationKey, conversationIv, conversation);
            }

        }



        public string AddFriend(string msg, int clientId)
        {
            lock(activeUsers[clientId])
            {
                if(!activeUsers[clientId].logged) return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOT_LOGGED_IN, Options.LOGIN);
            }
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            string userName = fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];
            if (userName == activeUsers[clientId].userName) return TransmisionProtocol.CreateServerMessage(ErrorCodes.SELF_INVITE_ERROR, Options.LOGIN);
            ExtendedInvitation ei = new ExtendedInvitation();

            // Check if we arent already friends
            lock (activeUsers[clientId])
            {
                if (activeUsers[clientId].dbConnection.CheckFriends(activeUsers[clientId].userName, userName)) return TransmisionProtocol.CreateServerMessage(
                      ErrorCodes.ALREADY_FRIENDS, Options.LOGIN);
                ei.sender = activeUsers[clientId].userName;
            }

            lock(invitations)
            {
                foreach(var i in invitations.Values)
                {
                    if((i.sender == userName && i.reciver == activeUsers[clientId].userName) || ((i.reciver == userName && i.sender == activeUsers[clientId].userName)))
                        return TransmisionProtocol.CreateServerMessage(ErrorCodes.INVITATION_ALREADY_EXIST, Options.SEND_FRIEND_INVITATION);
                }
            }

            var param = Security.GenerateParameters();
            ei.g = Security.GetG(param); 
            ei.p = Security.GetP(param);
            ei.reciver = userName;
            ei.invitationId = activeUsers[clientId].dbConnection.CreateNewInvitation(ei.sender, ei.reciver, ei.p, ei.g);
            
            lock (invitations)
            {
                AddFriend(ei,ei.invitationId);
            }         
            return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.SEND_FRIEND_INVITATION, ei.p,ei.g,ei.invitationId.ToString());
        }

        // Errors
        public string DhExchange(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            int invId = Int32.Parse(fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1]);
            string publicKeySender = fields[1].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];
            string encryptedSenderPrivateKey = fields[2].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];
            string ivToDecryptSenderPrivateKey = fields[3].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];

            try
            {
                lock (invitations[invId])
                {
                    invitations[invId].publicKeySender = publicKeySender;
                    invitations[invId].encryptedSenderPrivateKey = encryptedSenderPrivateKey;
                    invitations[invId].ivToDecryptSenderPrivateKey = ivToDecryptSenderPrivateKey;
                    if (activeUsers[clientId].dbConnection.InsertDHKeysToInvitation(invitations[invId].invitationId, invitations[invId].publicKeySender, 
                        invitations[invId].encryptedSenderPrivateKey, invitations[invId].ivToDecryptSenderPrivateKey))
                    return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.SEND_DH_PK_INVITING);
                    else return TransmisionProtocol.CreateServerMessage(ErrorCodes.DH_EXCHANGE_ERROR, Options.SEND_DH_PK_INVITING);
                }
            }
            catch
            {
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.DH_EXCHANGE_ERROR, Options.SEND_DH_PK_INVITING);
            }

        }

        // Errors
        public string DeclineFriend(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            int invId = Int32.Parse(fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1]);
            try
            {
                lock (invitations)
                {
                    activeUsers[clientId].dbConnection.DeleteInvitation(invId);
                    invitations.Remove(invId);
                }
            }
            catch
            {
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.DECLINE_FRIEND_ERROR, Options.DECLINE_FRIEND_INVITATION);
            }

            return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.DECLINE_FRIEND_INVITATION);
        }

        // TEST
        public string AcceptFriend(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            int invId = Int32.Parse(fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1]);
            string reciverPk = fields[1].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];

            string conversationId = "";
            var conversationIv = "";
            lock (activeUsers[clientId])
            {
                lock (invitations)
                {
                    conversationIv = Security.ByteArrayToHexString(Security.GenerateIV());
                    try
                    {
                        conversationId = activeUsers[clientId].dbConnection.AddFriends(activeUsers[clientId].userId, invitations[invId].sender, conversationIv);
                        if(conversationId == "") return TransmisionProtocol.CreateServerMessage(ErrorCodes.ADDING_FRIENDS_ERROR, Options.ACCPET_FRIEND_INVITATION);
                        if (activeUsers[clientId].dbConnection.InsertDHPublicReciverKey(invId, reciverPk))
                        {
                            invitations[invId].publicKeyReciver = reciverPk;
                            invitations[invId].accepted = true;
                            invitations[invId].conversationId = conversationId;
                            invitations[invId].conversationIv = conversationIv;
                        }
                        else return TransmisionProtocol.CreateServerMessage(ErrorCodes.ADDING_FRIENDS_ERROR, Options.ACCPET_FRIEND_INVITATION);

                    }
                    catch
                    {
                        return TransmisionProtocol.CreateServerMessage(ErrorCodes.WRONG_INVATATION_ID, Options.ACCPET_FRIEND_INVITATION);
                    }
                    
                }
            }

            return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.ACCPET_FRIEND_INVITATION, conversationId, conversationIv);
        }

        // Errors
        public string SendConversationKey(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            string conversationId = fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];
            string conversationKey = fields[1].Split(":", StringSplitOptions.RemoveEmptyEntries)[1];

 
            lock(activeUsers[clientId])
            {
                activeUsers[clientId].dbConnection.SetUserConversationKey(activeUsers[clientId].userId, conversationKey, int.Parse(conversationId));
            }
            return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.SEND_CONVERSATION_KEY);
        }


        public string SendInvitations(string msg, int clientId)
        {
            string username;
            lock(activeUsers[clientId])
            {
                if (!activeUsers[clientId].logged) return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOT_LOGGED_IN, Options.LOGIN);
                username = activeUsers[clientId].userName;
            }
            List<Invitation> invs = new List<Invitation>();
            int[] keys = new int[invitations.Count];
            invitations.Keys.CopyTo(keys, 0);
            lock(invitations)
            {
                foreach(var i in keys)
                {
                    if(invitations[i].reciver == username && !invitations[i].sended)
                    {
                        invs.Add(invitations[i]);
                        invitations[i].sended = true;
                    }
                }
            }
            if (invs.Count > 0)
            {
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.GET_FRIEND_INVITATIONS, JsonConvert.SerializeObject(invs));
            }
            else return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOTHING_TO_SEND, Options.LOGIN);
        }

        public string SendAcceptedFriends(string msg, int clientId)
        {
            string username;
            if (!activeUsers[clientId].logged) return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOT_LOGGED_IN, Options.GET_ACCEPTED_FRIENDS);
            username = activeUsers[clientId].userName;
            List<ExtendedInvitation> invs = new List<ExtendedInvitation>();

            int[] t = new int[invitations.Count];
            invitations.Keys.CopyTo(t, 0);
            lock(invitations)
            {
                foreach(int i in t)
                {
                    if(invitations[i].sender == username && invitations[i].accepted)
                    {
                        invs.Add(invitations[i]);
                        if (activeUsers[clientId].dbConnection.DeleteInvitation(invitations[i].invitationId))
                            invitations.Remove(i);
                        else return TransmisionProtocol.CreateServerMessage(ErrorCodes.DB_DELETE_INVITATION_ERROR, Options.GET_ACCEPTED_FRIENDS);
                    }
                }
                if(invs.Count > 0)
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.GET_ACCEPTED_FRIENDS, JsonConvert.SerializeObject(invs));

                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOTHING_TO_SEND, Options.GET_ACCEPTED_FRIENDS);
            }
        }


        public string Notification(string msg, int clientId)
        {
            if (!activeUsers[clientId].logged) return TransmisionProtocol.CreateServerMessage(ErrorCodes.NOT_LOGGED_IN, Options.LOGIN);
            int userId = activeUsers[clientId].userId;

            lock (notifications)
            {
                try
                {
                    string notify = JsonConvert.SerializeObject(notifications[userId].Values);
                    notifications[userId].Clear();
                    return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.GET_NOTIFICATIONS,notify );
                }
                catch
                {
                    return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_NOTIFICATIONS, Options.GET_NOTIFICATIONS, "");
                }

            }

        }

        //TODO Backup invitations

        //TODO store parameters and send them to second user
        public string ActivateConversation(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            int conversationId = int.Parse(fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1]);
            int userId = activeUsers[clientId].userId;
            try
            {
                lock(activeConversations)
                {
                    activeConversations[userId] = conversationId;
                    activeUsers[clientId].activeConversation = conversationId;
                }
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.ACTIVATE_CONVERSATION);
            }
            catch
            {
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.CANNOT_ACTIVATE_CONVERSATION, Options.ACTIVATE_CONVERSATION);
            }   
        }


        //TEST
        public string SendMessage(string msg, int clientId)
        {
            string[] fields = msg.Split("$$", StringSplitOptions.RemoveEmptyEntries);
            int conversationId = int.Parse(fields[0].Split(":", StringSplitOptions.RemoveEmptyEntries)[1]);
            string message = fields[1].Split(":", 2,StringSplitOptions.RemoveEmptyEntries)[1];
            int userId = activeUsers[clientId].dbConnection.GetSecondUserId(conversationId, activeUsers[clientId].userId);


            Message messageObject = JsonConvert.DeserializeObject<Message>(message);
            messageObject.date = DateTime.Now;
            message = JsonConvert.SerializeObject(messageObject);
            activeUsers[clientId].redis.AddMessage(conversationId, message);

            bool isActive = false;
            lock (messagesToSend)
            {
                foreach(var aUser in activeUsers)
                {
                    if(aUser.userId == userId)
                    {
                        isActive = true;
                        break;
                    }
                }
                if (isActive)
                {
                    if (messagesToSend.ContainsKey(userId))
                    {
                        messagesToSend[userId][conversationId].Add(JsonConvert.DeserializeObject<Message>(message));
                    }
                    else
                    {
                        messagesToSend.Add(userId, new Dictionary<int, List<Message>>()
                        {
                            [conversationId] =
                            new List<Message> { JsonConvert.DeserializeObject<Message>(message) }
                        });
                    }
                }

            }
            lock (activeConversations)
            {
                if (!activeConversations.ContainsKey(userId))
                {
                    activeConversations[userId] = -1;
                }
                if (!(conversationId == activeConversations[userId]))
                {
                    lock (notifications)
                    {
                        if (notifications.ContainsKey(userId))
                        {
                            notifications[userId][conversationId].numberOfMessages += 1;
                        }
                        else
                        {
                            notifications.Add(userId, new Dictionary<int, Notification>()
                            {
                                [conversationId] =
                                new Notification { numberOfMessages = 1, username = activeUsers[clientId].userName}
                            });
                        }
                    }
                }

            }
            return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.SEND_MESSAGE);
        }

        public string NewMessages(string msg, int clientId)
        {
            int id = activeUsers[clientId].userId;
            int activeConversationId = 0;
            lock (activeConversations)
            {
                activeConversationId = activeConversations[id];
            }
            try
            {               
                lock (messagesToSend[id])
                {
                    var messages = JsonConvert.SerializeObject(messagesToSend[id][activeConversationId]);
                    messagesToSend[id][activeConversationId].Clear();
                    return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_ERROR, Options.GET_NEW_MESSAGES,
                        messages);
                }                
            }
            catch
            {
                return TransmisionProtocol.CreateServerMessage(ErrorCodes.NO_MESSAGES, Options.SEND_MESSAGE);
            }

        }










        public int AddActiveUser()
        {
            for(int i=0;i<activeUsers.Count;i++)
            {
                if(activeUsers[i] == null)
                {
                    activeUsers[i] = new User();
                    return i;
                }
            }
            activeUsers.Add(new User());
            return activeUsers.Count - 1;
        }

        public void AddFriend(ExtendedInvitation ei,int index)
        {
            /*
            for (int i = 0; i < invitations.Count; i++)
            {
                if (activeUsers[i] == null)
                {
                    invitations[i] = ei;
                    return i;
                }
            }
            */
            invitations[index] = ei;
        }

        public bool DeleteActiveUser(int clientId)
        {
            try {
                lock (activeUsers[clientId])
                {
                    activeUsers[clientId].dbConnection.CloseConnection();
                    //activeUsers[clientId].redis.redis.Close();
                    activeUsers[clientId] = null;
                }
            }
            catch
            {
                return false;
            }
            return true;

        }


        public ClientProcessing()
        {
            functions = new List<Functions>();        
            functions.Add(new Functions(Logout));
            functions.Add(new Functions(Login));
            functions.Add(new Functions(CreateUser));
            functions.Add(new Functions(CheckUserName));
            functions.Add(new Functions(Disconnect));
            functions.Add(new Functions(GetFriends));
            functions.Add(new Functions(SendConversation));
            functions.Add(new Functions(ActivateConversation));
            functions.Add(new Functions(SendMessage));
            functions.Add(new Functions(NewMessages));
            functions.Add(new Functions(Notification));
            functions.Add(new Functions(AddFriend));
            functions.Add(new Functions(DhExchange));
            functions.Add(new Functions(SendInvitations));
            functions.Add(new Functions(DeclineFriend));
            functions.Add(new Functions(AcceptFriend));
            functions.Add(new Functions(SendConversationKey));
            functions.Add(new Functions(SendAcceptedFriends));

            dbMethods = new DbMethods();
            activeUsers = new List<User>();
            invitations = dbMethods.GetInvitations();
            messagesToSend = new Dictionary<int, Dictionary<int, List<Message>>>();
            notifications = new Dictionary<int, Dictionary<int, Notification>>();
            activeConversations = new Dictionary<int, int>();
        }


    }
}
