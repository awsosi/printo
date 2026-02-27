import type { DispatchRequest, PrinterDispatcher } from '../pipeline.js';

export class LoggingPrinterDispatcher implements PrinterDispatcher {
  async dispatch(input: DispatchRequest): Promise<void> {
    // eslint-disable-next-line no-console
    console.log(
      JSON.stringify({
        service: 'worker',
        event: 'print_dispatch',
        routeType: input.routeType,
        printerId: input.printer.id,
        printerName: input.printer.name,
        filePath: input.file.path,
        pageNumber: input.page.pageNumber
      })
    );
  }
}
