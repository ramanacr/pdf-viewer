// PDF Viewer embed SDK in an Angular app (standalone component). Copy into your app: this file is
// not run here.
//
// 1. Load the SDK once, in index.html, from where you serve the viewer:
//      <script src="https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.js"></script>
//    It defines window.PdfViewer. There is no npm package and nothing to install.
// 2. Use the component:
//      <app-pdf-viewer [url]="'/files/report.pdf'" fileName="report.pdf"
//                      [config]="{ embedMode: 'SIZED_CONTAINER' }" (viewerEvent)="log($event)"></app-pdf-viewer>

import { AfterViewInit, Component, EventEmitter, Input, OnChanges, OnDestroy, Output } from '@angular/core';

// The SDK has no type package; these are the parts this component uses.
interface PdfViewerEvent { type: string; data: Record<string, unknown>; }
interface PdfViewerApis { getPDFMetadata(): Promise<Record<string, unknown>>; gotoLocation(page: number, x?: number, y?: number): Promise<void>; }
interface PdfViewerView {
    previewFile(file: unknown, config: unknown): Promise<{ getAPIs(): Promise<PdfViewerApis> }>;
    registerCallback(type: string, handler: (e: PdfViewerEvent) => void, options: unknown): void;
    destroy(): void;
}
declare const PdfViewer: { View: new (options: { divId: string }) => PdfViewerView };

let nextId = 0;

@Component({
    selector: 'app-pdf-viewer',
    standalone: true,
    template: '<div [id]="divId" [style.height.px]="height"></div>',
})
export class PdfViewerComponent implements AfterViewInit, OnChanges, OnDestroy {
    @Input() url = '';
    @Input() fileName = 'document.pdf';
    @Input() config: Record<string, unknown> = {};
    @Input() height = 600;
    @Output() viewerEvent = new EventEmitter<PdfViewerEvent>();
    @Output() ready = new EventEmitter<PdfViewerApis>();
    @Output() failed = new EventEmitter<{ code: string; message: string }>();

    readonly divId = `pdfviewer-${++nextId}`;
    private view: PdfViewerView | null = null;

    ngAfterViewInit(): void {
        this.view = new PdfViewer.View({ divId: this.divId });
        this.view.registerCallback('EVENT_LISTENER', e => this.viewerEvent.emit(e), {});
        this.show();
    }

    ngOnChanges(): void {
        if (this.view) this.show();
    }

    ngOnDestroy(): void {
        this.view?.destroy();
        this.view = null;
    }

    private show(): void {
        if (!this.view || !this.url) return;
        this.view.previewFile({ content: { location: { url: this.url } }, metaData: { fileName: this.fileName } }, this.config)
            .then(viewer => viewer.getAPIs())
            .then(apis => this.ready.emit(apis))
            .catch(error => { if (error.code !== 'CLOSED') this.failed.emit({ code: error.code, message: error.message }); });
    }
}
