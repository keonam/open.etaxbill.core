﻿/*
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with this program.If not, see<http://www.gnu.org/licenses/>.
*/

using System;
using System.Data;
using NpgsqlTypes;
using OdinSdk.BaseLib.Configuration;
using OpenTax.Engine.Library.Data.POSTGRESQL;
using OdinSdk.eTaxBill.Security.Issue;
using OdinSdk.eTaxBill.Security.Signature;

namespace OpenTax.Engine.Signer
{
    public class Engine : IDisposable
    {
        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------
        private readonly static object SyncEngine = new object();

        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------
        private OpenTax.Engine.Library.Data.POSTGRESQL.PgDataHelper m_dataHelper = null;
        private OpenTax.Engine.Library.Data.POSTGRESQL.PgDataHelper LSQLHelper
        {
            get
            {
                if (m_dataHelper == null)
                    m_dataHelper = new OpenTax.Engine.Library.Data.POSTGRESQL.PgDataHelper();

                return m_dataHelper;
            }
        }

        private OpenTax.Engine.Library.Data.POSTGRESQL.PgDeltaHelper m_dltaHelper = null;
        private OpenTax.Engine.Library.Data.POSTGRESQL.PgDeltaHelper LDltaHelper
        {
            get
            {
                if (m_dltaHelper == null)
                    m_dltaHelper = new OpenTax.Engine.Library.Data.POSTGRESQL.PgDeltaHelper();

                return m_dltaHelper;
            }
        }

        private OpenTax.Channel.Interface.ISigner m_iSigner = null;
        private OpenTax.Channel.Interface.ISigner ISigner
        {
            get
            {
                if (m_iSigner == null)
                    m_iSigner = new OpenTax.Channel.Interface.ISigner();

                return m_iSigner;
            }
        }
        
        private OpenTax.Engine.Library.UAppHelper m_appHelper = null;
        public OpenTax.Engine.Library.UAppHelper UAppHelper
        {
            get
            {
                if (m_appHelper == null)
                    m_appHelper = new OpenTax.Engine.Library.UAppHelper(ISigner.Manager);

                return m_appHelper;
            }
        }

        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------
        private DataSet m_IssuingSet = null;
        private DataSet IssuingSet
        {
            get
            {
                if (m_IssuingSet == null)
                    m_IssuingSet = Schema.SNG.GetTaxSchema();

                return m_IssuingSet;
            }
        }

        private DataTable m_issuingTbl = null;
        private DataTable IssuingTbl
        {
            get
            {
                if (m_issuingTbl == null)
                    m_issuingTbl = IssuingSet.Tables["TB_eTAX_ISSUING"];

                return m_issuingTbl;
            }
        }

        private DataTable m_invoiceTbl = null;
        private DataTable InvoiceTbl
        {
            get
            {
                if (m_invoiceTbl == null)
                    m_invoiceTbl = Schema.SNG.GetTaxModifiedDataTable(
                        IssuingSet, "TB_eTAX_INVOICE",
                        new string[] 
                            { 
                                "issueId", "isIssued", "isSuccess"
                            }
                        );

                return m_invoiceTbl;
            }
        }

        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------
        public bool LogCommands
        {
            get
            {
                return CfgHelper.SNG.DebugMode;
            }
        }

        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------
        private struct SignatureArgs
        {
            public X509CertMgr invoicerCert;
            public string invoicerId;
            public int noInvoicee;
            public int noIssuing;
            public string where;
            public PgDatParameters dbps;
        }

        private int CheckReEnter(string p_invoicerId, string p_where, PgDatParameters p_dbps)
        {
            int _result = -1;

            var _sqlstr
                    = "SELECT COUNT(a.isIssued) as norec "
                    + "  FROM TB_eTAX_INVOICE a "
                    + " WHERE a.isIssued=@isIssuedX "
                    + "   AND ( "
                    + "         (RIGHT(a.typeCode, 2) IN ('01', '02', '04') AND a.invoicerId=@invoicerId) "
                    + "         OR "
                    + "         (RIGHT(a.typeCode, 2) IN ('03', '05') AND a.brokerId=@invoicerId) "
                    + "       ) "
                    + p_where;

            p_dbps.Add("@isIssuedX", NpgsqlDbType.Varchar, "X");
            p_dbps.Add("@invoicerId", NpgsqlDbType.Varchar, p_invoicerId);

            var _ds = LSQLHelper.SelectDataSet(UAppHelper.ConnectionString, _sqlstr, p_dbps);

            int _norec = Convert.ToInt32(_ds.Tables[0].Rows[0]["norec"]);
            if (_norec < 1)
            {
                string _updstr
                        = "UPDATE TB_eTAX_INVOICE "
                        + "   SET isIssued=@isIssuedX "
                        + "  FROM TB_eTAX_INVOICE a "
                        + " WHERE a.isSuccess != @isSuccess "
                        + "   AND ( "
                        + "         (RIGHT(a.typeCode, 2) IN ('01', '02', '04') AND a.invoicerId=@invoicerId) "
                        + "         OR "
                        + "         (RIGHT(a.typeCode, 2) IN ('03', '05') AND a.brokerId=@invoicerId) "
                        + "       ) "
                        + p_where;

                p_dbps.Add("@isIssuedX", NpgsqlDbType.Varchar, "X");
                p_dbps.Add("@isSuccess", NpgsqlDbType.Varchar, "T");
                p_dbps.Add("@invoicerId", NpgsqlDbType.Varchar, p_invoicerId);

                _result = LSQLHelper.ExecuteText(UAppHelper.ConnectionString, _updstr, p_dbps);
            }
            else
            {
                if (LogCommands == true)
                    ELogger.SNG.WriteLog(String.Format("re-enter: invoicerId->'{0}', working-row(s)->{1}", p_invoicerId, _norec));
            }

            return _result;
        }

        private int CheckSignature(X509CertMgr p_invoicerCert, string p_invoicerId, int p_noInvoicee, string p_where, PgDatParameters p_dbps)
        {
            int _noIssuing = 0;

            lock (SyncEngine)
                _noIssuing = CheckReEnter(p_invoicerId, p_where, p_dbps);

            if (_noIssuing > 0)
            {
                SignatureArgs _args = new SignatureArgs()
                {
                    invoicerCert = p_invoicerCert,
                    invoicerId = p_invoicerId,
                    noInvoicee = p_noInvoicee,
                    noIssuing = _noIssuing,
                    where = p_where,
                    dbps = p_dbps
                };

                // Do not use using statement
                ThreadPoolWait _doneEvent = new ThreadPoolWait();
                _doneEvent.QueueUserWorkItem(DoSignature, _args);

                if (Environment.UserInteractive == true)
                    _doneEvent.WaitOne();
            }

            return _noIssuing;
        }

        private void DoSignature(object p_args)
        {
            var _args = (SignatureArgs)p_args;
            _args.noInvoicee = _args.noIssuing;
            _args.noIssuing = 0;

            try
            {
                int _toprow = 800;

                var _sqlstr
                        = "SELECT a.issueId, a.typeCode, a.invoicerId, a.invoicerEMail, a.invoiceeId, a.invoiceeEMail1 as invoiceeEMail, "
                        + "               a.brokerId, a.brokerEMail, b.providerId, c.aspEMail as providerEMail "
                        + "  FROM TB_eTAX_INVOICE a "
                        + "       LEFT JOIN TB_eTAX_CUSTOMER b ON a.invoiceeId=b.customerId "
                        + "       LEFT JOIN (SELECT * FROM TB_eTAX_PROVIDER WHERE NULLIF(providerId, '') IS NOT NULL) c ON b.providerId=c.providerId "
                        + " WHERE a.isIssued=@isIssuedX "       // to avoid infinite loop, do check isIssued here.
                        + "   AND ( "
                        + "         (RIGHT(a.typeCode, 2) IN ('01', '02', '04') AND a.invoicerId=@invoicerId) "
                        + "         OR "
                        + "         (RIGHT(a.typeCode, 2) IN ('03', '05') AND a.brokerId=@invoicerId) "
                        + "       ) "
                        + _args.where
                        + " ORDER BY a.issueId"
                        + " LIMIT " + _toprow;
                {
                    _args.dbps.Add("@isIssuedX", NpgsqlDbType.Varchar, "X");
                    _args.dbps.Add("@invoicerId", NpgsqlDbType.Varchar, _args.invoicerId);
                }

                //if (LogCommands == true)
                //    ELogger.SNG.WriteLog(String.Format("begin: invoicerId->'{0}', noInvoicee->{1}", _args.invoicerId, _args.noInvoicee));

                // 만약 InsertDeltaSet을 처리하는 중에 오류가 발생하면 무한 loop를 발생 하게 되므로,
                // 'X'로 marking한 레코드의 총 갯수를 감소하여 '0'보다 큰 경우에만 반복한다.
                while (_args.noInvoicee > 0)
                {
                    InvoiceTbl.Clear();
                    IssuingTbl.Clear();

                    var _workingSet = LSQLHelper.SelectDataSet(UAppHelper.ConnectionString, _sqlstr, _args.dbps);
                    if (LSQLHelper.IsNullOrEmpty(_workingSet) == true)
                        break;

                    var _rows = _workingSet.Tables[0].Rows;

                    var _doneEvents = new ThreadPoolWait[_rows.Count];
                    for (int i = 0; i < _rows.Count; i++)
                    {



                        _doneEvents[i] = new ThreadPoolWait();

                        Updater _worker = new Updater(_args.invoicerCert, IssuingTbl, InvoiceTbl);
                        _doneEvents[i].QueueUserWorkItem(_worker.SignatureCallBack, _rows[i]);

                        if (Environment.UserInteractive == true)
                            _doneEvents[i].WaitOne();
                    }

                    ThreadPoolWait.WaitForAll(_doneEvents);

                    // 처리된 레코드가 한개 이하 인 경우는 종료한다. (문제가 있는 경우로 보여 짐)
                    if (_rows.Count < 1)
                        break;

                    //if (LogCommands == true)
                    //    ELogger.SNG.WriteLog(String.Format("loop: invoicerId->'{0}', noInvoicee->{1}, noIssuing->{2}", _args.invoicerId, _args.noInvoicee, _rows.Count));

                    _args.noInvoicee -= _rows.Count;
                    _args.noIssuing += IssuingTbl.Rows.Count;

                    LDltaHelper.InsertDeltaSet(UAppHelper.ConnectionString, IssuingSet);
                }
            }
            catch (SignerException ex)
            {
                ELogger.SNG.WriteLog(ex);
            }
            catch (Exception ex)
            {
                ELogger.SNG.WriteLog(ex);
            }
            finally
            {
                if (LogCommands == true)
                    ELogger.SNG.WriteLog(String.Format("end: invoicerId->'{0}', noInvoicee->{1}, noIssuing->{2}", _args.invoicerId, _args.noInvoicee, _args.noIssuing));

                int _noClearing = ClearXFlag(_args.invoicerId);
                if (_noClearing > 0)
                {
                    if (LogCommands == true)
                        ELogger.SNG.WriteLog(String.Format("clearX: invoicerId->'{0}', noClear->{1}", _args.invoicerId, _noClearing));
                }
            }
        }

        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// 해당 사업자의 서명방법을 읽어 온다.
        /// </summary>
        /// <param name="p_invoicerId"></param>
        /// <returns></returns>
        public DataSet GetCustomerSet(string p_invoicerId)
        {
            ISigner.WriteDebug(p_invoicerId);

            var _sqlstr
                    = "SELECT signingType, signFromDay, signTillDay "
                    + "  FROM TB_eTAX_CUSTOMER "
                    + " WHERE customerId=@customerId";

            var _dbps = new PgDatParameters();
            {
                _dbps.Add("@customerId", NpgsqlDbType.Varchar, p_invoicerId);
            }

            var _customer_set = LSQLHelper.SelectDataSet(UAppHelper.ConnectionString, _sqlstr, _dbps);
            if (LSQLHelper.IsNullOrEmpty(_customer_set) == true)
                throw new SignerException(String.Format("not exist customer: invoicerId->'{0}'", p_invoicerId));

            return _customer_set;
        }

        /// <summary>
        /// 해당 사업자의 미서명된 세금계산서를 정의된 갯수 만큼 서명 합니다.
        /// 100건 이상인 경우에는 100건에 한번씩 데이터베이스에 저장 합니다.
        /// </summary>
        /// <param name="p_invoicerCert"></param>
        /// <param name="p_invoicerId"></param>
        /// <param name="p_noInvoicee"></param>
        //public int DoSignInvoice(X509CertMgr p_invoicerCert, string p_invoicerId, int p_noInvoicee)
        //{
        //    string _where = "";
        //    var _dbps = new PgDatParameters();

        //    return CheckSignature(p_invoicerCert, p_invoicerId, p_noInvoicee, _where, _dbps);
        //}

        /// <summary>
        /// 
        /// </summary>
        /// <param name="p_invoicerCert"></param>
        /// <param name="p_invoicerId"></param>
        /// <param name="p_issueIds">comma delimeter</param>
        public int DoSignInvoice(X509CertMgr p_invoicerCert, string p_invoicerId, string[] p_issueIds)
        {
            ISigner.WriteDebug(p_invoicerId);

            int _result = 0;

            string _issueCols = "";
            foreach (string _issueId in p_issueIds)
            {
                if (String.IsNullOrEmpty(_issueCols) == false)
                    _issueCols += ", ";

                _issueCols += String.Format("'{0}'", _issueId);
            }

            if (String.IsNullOrEmpty(_issueCols) == false)
            {
                string _where = String.Format(" AND a.issueId IN ({0})", _issueCols);
                var _dbps = new PgDatParameters();

                _result = CheckSignature(p_invoicerCert, p_invoicerId, p_issueIds.Length, _where, _dbps);
            }

            return _result;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="p_invoicerCert"></param>
        /// <param name="p_invoicerId"></param>
        /// <param name="p_noInvoicee"></param>
        /// <param name="p_fromDay"></param>
        /// <param name="p_tillDay"></param>
        /// <returns></returns>
        public int DoSignInvoice(X509CertMgr p_invoicerCert, string p_invoicerId, int p_noInvoicee, DateTime p_fromDay, DateTime p_tillDay)
        {
            ISigner.WriteDebug(p_invoicerId);

            var _where = " AND a.issueDate>=@fromDate AND a.issueDate<=@tillDate ";

            var _dbps = new PgDatParameters();
            {
                _dbps.Add("@fromDate", NpgsqlDbType.TimestampTz, p_fromDay);
                _dbps.Add("@tillDate", NpgsqlDbType.TimestampTz, p_tillDay);
            }

            return CheckSignature(p_invoicerCert, p_invoicerId, p_noInvoicee, _where, _dbps);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="p_invoicerId"></param>
        /// <returns></returns>
        public int ClearXFlag(string p_invoicerId)
        {
            ISigner.WriteDebug(p_invoicerId);
            
            var _sqlstr
                    = "UPDATE TB_eTAX_INVOICE "
                    + "   SET isIssued=@isIssued, isSuccess=@isSuccess "
                    + " WHERE isIssued=@isIssuedX "
                    + "   AND ( "
                    + "         (RIGHT(typeCode, 2) IN ('01', '02', '04') AND invoicerId=@invoicerId) "
                    + "         OR "
                    + "         (RIGHT(typeCode, 2) IN ('03', '05') AND brokerId=@invoicerId) "
                    + "       )";

            var _dbps = new PgDatParameters();
            {
                _dbps.Add("@isIssued", NpgsqlDbType.Varchar, "F");
                _dbps.Add("@isSuccess", NpgsqlDbType.Varchar, "F");
                _dbps.Add("@isIssuedX", NpgsqlDbType.Varchar, "X");
                _dbps.Add("@invoicerId", NpgsqlDbType.Varchar, p_invoicerId);
            }

            return LSQLHelper.ExecuteText(UAppHelper.ConnectionString, _sqlstr, _dbps);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public int ClearXFlag()
        {
            ISigner.WriteDebug("*");
            
            var _sqlstr
                    = "UPDATE TB_eTAX_INVOICE "
                    + "   SET isIssued=@isIssued, isSuccess=@isSuccess "
                    + " WHERE isIssued=@isIssuedX";

            var _dbps = new PgDatParameters();
            {
                _dbps.Add("@isIssued", NpgsqlDbType.Varchar, "F");
                _dbps.Add("@isSuccess", NpgsqlDbType.Varchar, "F");
                _dbps.Add("@isIssuedX", NpgsqlDbType.Varchar, "X");
            }

            return LSQLHelper.ExecuteText(UAppHelper.ConnectionString, _sqlstr, _dbps);
        }

        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (m_iSigner != null)
                {
                    m_iSigner.Dispose();
                    m_iSigner = null;
                }
                if (m_IssuingSet != null)
                {
                    m_IssuingSet.Dispose();
                    m_IssuingSet = null;
                }
                if (m_issuingTbl != null)
                {
                    m_issuingTbl.Dispose();
                    m_issuingTbl = null;
                }
                if (m_invoiceTbl != null)
                {
                    m_invoiceTbl.Dispose();
                    m_invoiceTbl = null;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        ~Engine()
        {
            Dispose(false);
        }

        //-------------------------------------------------------------------------------------------------------------------------
        //
        //-------------------------------------------------------------------------------------------------------------------------
    }
}