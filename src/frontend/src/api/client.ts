import axios from 'axios';

const client = axios.create({ baseURL: '/api/v1' });

client.interceptors.request.use((config) => {
  const token = localStorage.getItem('esar_token');
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

client.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem('esar_token');
      if (!window.location.pathname.startsWith('/login')) {
        window.location.href = '/login';
      }
    }
    return Promise.reject(error);
  },
);

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface AssetDto {
  id: string;
  hostname: string;
  fqdn?: string;
  operatingSystem?: string;
  assetType: string;
  status: string;
  lifecycleStatus: string;
  environment: string;
  criticality: string;
  ownerName?: string;
  businessUnit?: string;
  complianceScore: number;
  complianceStatus: string;
  firstSeen: string;
  lastSeen: string;
  primaryIp?: string;
  sources: string[];
}

export interface DashboardSummary {
  totalAssets: number;
  activeAssets: number;
  criticalAssets: number;
  cloudAssets: number;
  nonCompliantAssets: number;
  pendingReviewMatches: number;
  openIncidents: number;
  avgComplianceScore: number;
  staleAssets: number;
  duplicateCandidates: number;
}

export default client;
