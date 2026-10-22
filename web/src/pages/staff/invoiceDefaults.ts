import type { InvoiceData, InvoiceLineInput } from '../../api/invoices'

export const blank = (): InvoiceLineInput => ({ description: '', quantity: 1, unitPrice: 0, lineAmount: 0, taxCode: 'S', taxRate: 0.15, taxAmount: 0, poLineRef: null, dimensionsJson: null })


export const emptyInvoice = (): InvoiceData => ({
  invoiceNumber: '', invoiceDate: new Date().toISOString().slice(0, 10), dueDate: null, currencyCode: 'SAR', subtotal: 0, taxTotal: 0, total: 0,
  poReference: null, supplierTaxInvoiceRef: null, supplierTaxNumber: null, lines: [blank()],
})

