import { api } from './client'

const V1 = '/api/v1'
const json = (body: unknown): RequestInit => ({ headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

export const NOTIFICATION_KINDS = [
  'ApprovalAssigned', 'ApprovalReminder', 'ApprovalEscalated', 'ExceptionAssigned', 'BankChangePending', 'PaymentAwaitingApproval', 'PaymentReleased', 'ErpDeadLetter', 'ReconciliationDifferences',
] as const
export const CHANNELS = ['InApp', 'Email', 'Webhook', 'Push'] as const
export type Channel = (typeof CHANNELS)[number]

export interface NotificationItem { id: string; kind: string; title: string; body: string; path: string; createdAt: string; readAt: string | null; mandatory: boolean }
export interface Preference { kind: string; channel: Channel; enabled: boolean; mandatory: boolean }
export interface TemplateItem { kind: string; channel: Channel; locale: 'en' | 'ar'; subject: string; body: string; isOverride: boolean }

export const listNotifications = (locale: string, unread = false) => api<NotificationItem[]>(`${V1}/notifications?locale=${locale}&take=50${unread ? '&unread=true' : ''}`)
export const getUnreadCount = () => api<{ count: number }>(`${V1}/notifications/unread-count`)
export const markNotificationRead = (id: string) => api<void>(`${V1}/notifications/${id}/read`, { method: 'POST', ...json({}) })
export const markAllNotificationsRead = () => api<{ marked: number }>(`${V1}/notifications/read-all`, { method: 'POST', ...json({}) })
export const getPreferences = () => api<Preference[]>(`${V1}/notifications/preferences`)
export const setPreference = (kind: string, channel: string, enabled: boolean) => api<Preference>(`${V1}/notifications/preferences`, { method: 'PUT', ...json({ kind, channel, enabled }) })

export const getTemplates = () => api<TemplateItem[]>(`${V1}/admin/notification-templates`)
export const saveTemplate = (t: { kind: string; channel: string; locale: string; subject: string; body: string }) => api<TemplateItem>(`${V1}/admin/notification-templates`, { method: 'PUT', ...json(t) })
export const resetTemplate = (kind: string, channel: string, locale: string) => api<void>(`${V1}/admin/notification-templates?kind=${kind}&channel=${channel}&locale=${locale}`, { method: 'DELETE' })
export const previewTemplate = (t: { kind: string; channel: string; locale: string; subject: string; body: string }) =>
  api<{ subject: string; body: string }>(`${V1}/admin/notification-templates/preview`, { method: 'POST', ...json(t) })

export interface ChannelConfigItem {
  id: string
  channel: string
  versionNo: number
  status: string
  config: Record<string, unknown>
  note: string
  createdBy: string
  createdAt: string
}
export interface ChannelState { active: ChannelConfigItem | null; pending: ChannelConfigItem | null }
export interface DeliverySummary { pending: number; sent: number; skipped: number; deadLetter: number }
export interface DeadLetter { id: string; channel: string; status: string; attempts: number; lastError: string | null; createdAt: string; kind: string; subjectLabel: string }

export const getChannel = (channel: string) => api<ChannelState>(`${V1}/admin/notification-channels/${channel}`)
export const proposeChannel = (channel: string, config: Record<string, unknown>, note: string) => api<ChannelConfigItem>(`${V1}/admin/notification-channels/${channel}`, { method: 'PUT', ...json({ config, note }) })
export const listChannelChanges = () => api<ChannelConfigItem[]>(`${V1}/notification-channel-changes`)
export const approveChannelChange = (id: string, comment: string) => api<ChannelConfigItem>(`${V1}/notification-channel-changes/${id}/approve`, { method: 'POST', ...json({ comment }) })
export const rejectChannelChange = (id: string, comment: string) => api<ChannelConfigItem>(`${V1}/notification-channel-changes/${id}/reject`, { method: 'POST', ...json({ comment }) })
export const cancelChannelChange = (id: string) => api<ChannelConfigItem>(`${V1}/notification-channel-changes/${id}/cancel`, { method: 'POST', ...json({}) })
export const getDeliverySummary = () => api<DeliverySummary>(`${V1}/admin/notification-deliveries/summary`)
export const getDeadLetters = () => api<DeadLetter[]>(`${V1}/admin/notification-deliveries/dead-letters`)
