// Page definitions for the mixed-carrier demo document, shared by the fixture
// generator script and the worker end-to-end test.
export const mixedCarrierPages = [
  {
    name: 'invoice',
    width: 595,
    height: 842,
    fontSize: 12,
    lines: [
      'FAKTURA VAT nr 2026/07/0042',
      'Sprzedawca: Vitkac Sp. z o.o., ul. Przykladowa 1, Warszawa',
      'Nabywca: Jan Kowalski, ul. Testowa 2, Krakow',
      'Suma brutto: 1 234,56 PLN',
      'Termin platnosci: 14 dni',
      'IBAN: PL61 1090 1014 0000 0712 1981 2874'
    ]
  },
  {
    name: 'dhl-outgoing-label',
    width: 288,
    height: 432,
    fontSize: 10,
    lines: [
      'DHL EXPRESS WORLDWIDE',
      'Ship from: Vitkac Warehouse, Warszawa',
      'Ship to: Jan Kowalski',
      'ul. Testowa 2, 30-001 Krakow, PL',
      'Waybill: 12 3456 7890',
      'Tracking number: JJD0099887766554433',
      'Service level: EXPRESS 12:00'
    ]
  },
  {
    name: 'packing-slip',
    width: 595,
    height: 842,
    fontSize: 12,
    lines: [
      'Packing slip / Specyfikacja',
      'Order confirmation: ZAM-2026-0731',
      'Pozycje: 3',
      '1x Sneakers 42, 1x Hoodie M, 1x Cap'
    ]
  },
  {
    name: 'ups-return-label',
    width: 595,
    height: 842,
    fontSize: 12,
    lines: [
      'UPS RETURN LABEL',
      'Etykieta zwrotna / Retoure',
      'Ship to: Returns Center, Vitkac Logistics',
      'ul. Magazynowa 8, 05-800 Pruszkow, PL',
      'Shipper: Jan Kowalski',
      'Tracking number: 1Z999AA10123456784',
      'UPS Standard'
    ]
  },
  {
    name: 'fedex-outgoing-label',
    width: 288,
    height: 432,
    fontSize: 10,
    lines: [
      'FedEx International Priority',
      'Ship from: Vitkac Warehouse, Warszawa',
      'Ship to: John Smith',
      '10 Main Street, London, UK',
      'Tracking number: 96123456789012345678',
      'Service level: PRIORITY OVERNIGHT'
    ]
  }
];
